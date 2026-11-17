using System.Diagnostics;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Exact;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Solver;

/// <summary>A choice of physical sources to search over: warehouse only, joint stock + one roll offer, or roll only.</summary>
internal sealed record SourceConfig(string Name, int[] Sources, bool HasRoll, bool HasRemnants);

internal enum DestroyOp
{
    RandomPieces,
    RollTail,
    RebuildSource,
    Window,
    EmptyRoll,
    DropRemnant,
}

/// <summary>
/// Hybrid time-bounded search (spec §6): exact small-case DP → deterministic multi-start construction over source
/// configurations, orderings, assignment strategies, fit and split rules → seeded random restarts → beam search →
/// destroy-and-repair improvement. Every complete plan is finalised and passed through the independent validator
/// before it may enter the incumbent pool.
/// </summary>
internal sealed class SearchEngine
{
    private static readonly (FitRule Fit, SplitRule Split)[] FitSplits =
    [
        (FitRule.BestAreaFit, SplitRule.ShorterLeftoverAxis),
        (FitRule.BestShortSideFit, SplitRule.MaximizeLargerResidual),
        (FitRule.BottomLeft, SplitRule.HorizontalFirst),
        (FitRule.BestLongSideFit, SplitRule.LongerLeftoverAxis),
        (FitRule.BestAreaFit, SplitRule.MinimizeSmallerResidual),
        (FitRule.BestShortSideFit, SplitRule.VerticalFirst),
    ];

    private readonly NormalizedProblem _p;
    private readonly OptimizationOptions _opt;
    private readonly SearchBudget _budget;
    private readonly Rng _rng;
    private readonly CutPlanValidator _validator = new();
    private readonly List<SourceConfig> _configs = [];

    public CandidatePool Pool { get; }
    public int Constructions { get; private set; }
    public int CompletePlans { get; private set; }
    public int Validated { get; private set; }
    public int Rejected { get; private set; }
    public int BeamRuns { get; private set; }
    public int ImprovementIterations { get; private set; }
    public List<string> RejectionSamples { get; } = [];
    public List<PhaseTiming> Phases { get; } = [];
    public ExactOutcome? Exact { get; private set; }
    public StopReason Stop { get; private set; } = StopReason.NotStarted;

    public SearchEngine(NormalizedProblem p, OptimizationOptions opt, SearchBudget budget)
    {
        _p = p;
        _opt = opt;
        _budget = budget;
        _rng = new Rng(p.Snapshot.Search.Seed);
        Pool = new CandidatePool(new PlanRanking.PlanComparer(opt.Objective), Math.Max(8, opt.PoolCapacity));
    }

    public void Run()
    {
        BuildConfigs();
        Phase("exact", RunExact);
        Phase("constructive", RunConstructive);
        Phase("beam", RunBeam);
        bool converged = false;
        Phase("improve", () => converged = RunImprovement());
        _budget.NoteTimeCap();
        Stop = _budget.Expired ? _budget.Reason() : converged ? StopReason.Converged : StopReason.ScheduleExhausted;
    }

    private void Phase(string name, Func<int> body)
    {
        var sw = Stopwatch.StartNew();
        int iterations = _budget.Expired ? 0 : body();
        Phases.Add(new PhaseTiming(name, sw.ElapsedMilliseconds, iterations));
    }

    private void Phase(string name, Action body) => Phase(name, () =>
    {
        body();
        return 1;
    });

    private void BuildConfigs()
    {
        var rem = _p.RemnantSources;
        if (rem.Length > 0) _configs.Add(new SourceConfig("warehouse-only", rem, false, true));
        foreach (int r in _p.RollSources)
        {
            string offer = _p.Sources[r].Offer!.OfferId;
            if (rem.Length > 0) _configs.Add(new SourceConfig($"joint:{offer}", [.. rem, r], true, true));
            _configs.Add(new SourceConfig($"roll-only:{offer}", [r], true, false));
        }
    }

    private IReadOnlyList<SourceSpec> SourcesOf(SourceConfig c) => c.Sources.Select(i => _p.Sources[i]).ToList();

    private static (TierOrder, OpenPreference)[] Strategies(SourceConfig c) => (c.HasRoll, c.HasRemnants) switch
    {
        (true, false) => [(TierOrder.MinExtension, OpenPreference.NoPreference), (TierOrder.FitFirst, OpenPreference.NoPreference)],
        (false, _) =>
        [
            (TierOrder.OpenedFirst, OpenPreference.SmallestFirst), (TierOrder.OpenedFirst, OpenPreference.LargestFirst),
            (TierOrder.FitFirst, OpenPreference.NoPreference), (TierOrder.MinExtension, OpenPreference.SmallestFirst),
        ],
        _ =>
        [
            (TierOrder.MinExtension, OpenPreference.SmallestFirst), (TierOrder.RemnantsFirst, OpenPreference.SmallestFirst),
            (TierOrder.MinExtension, OpenPreference.LargestFirst), (TierOrder.RemnantsFirst, OpenPreference.LargestFirst),
            (TierOrder.FitFirst, OpenPreference.NoPreference), (TierOrder.OpenedFirst, OpenPreference.SmallestFirst),
        ],
    };

    // ---- submission: finalise → assemble → independent validation → pool ------------------------------------
    private (PoolEntry? Entry, bool NewBest) Submit(PackingState st)
    {
        var fp = Finalizer.Finalize(st);
        if (fp is null) return (null, false);
        CompletePlans++;
        var plan = PlanAssembler.Assemble(fp);
        if (Pool.Contains(plan.CandidateId)) return (null, false);
        var outcome = _validator.Validate(_p.Snapshot, plan);
        if (!outcome.Passed)
        {
            Rejected++;
            if (RejectionSamples.Count < 5)
                RejectionSamples.Add($"{st.Origin}: " + string.Join("; ", outcome.Failures.Take(3).Select(f => $"{f.Code} {f.Message}")));
            return (null, false);
        }

        Validated++;
        plan = plan with { Validation = outcome };
        string group = plan.Purchase is null ? "warehouse-only" : "roll:" + plan.Purchase.RollOfferId;
        var entry = new PoolEntry { Plan = plan, State = st, Group = group };
        var (added, newBest) = Pool.Add(entry);
        return (added ? entry : null, newBest);
    }

    // ---- phase 0: exact --------------------------------------------------------------------------------------
    private void RunExact()
    {
        long deadline = _opt.Mode == ExecutionMode.Deterministic
            ? _budget.TimeBudgetMs
            : _budget.ElapsedMs + (long)(_budget.TimeBudgetMs * Math.Clamp(_opt.Exact.TimeShare, 0, 1));
        Exact = ExactGuillotineSolver.TrySolve(_p, _opt.Exact, _budget, deadline);
        if (Exact.Plan is { } plan) Submit(plan);
    }

    // ---- phase 1: constructive multi-start -------------------------------------------------------------------
    private int RunConstructive()
    {
        var perConfig = _configs.Select(c =>
        {
            var strategies = Strategies(c);
            // Row sections only change how a roll is cut, so they double the starts of roll configurations only.
            int rowVariants = c.HasRoll ? 2 : 1;
            var items = new List<(int O, int S, int F, int R)>();
            for (int o = 0; o < PieceOrdering.Deterministic.Length; o++)
                for (int s = 0; s < strategies.Length; s++)
                    for (int f = 0; f < FitSplits.Length; f++)
                        for (int r = 0; r < rowVariants; r++)
                            items.Add((o, s, f, r));
            // Diagonal enumeration: early items differ in ordering, strategy, fit/split and row sections simultaneously.
            return items.OrderBy(x => x.O + x.S + x.F + x.R).ThenBy(x => x.O).ThenBy(x => x.S).ThenBy(x => x.F).ThenBy(x => x.R).ToList();
        }).ToList();

        int iterations = 0;
        int longest = perConfig.Count == 0 ? 0 : perConfig.Max(l => l.Count);
        var all = Enumerable.Range(0, _p.Instances.Length).ToArray();
        for (int round = 0; round < longest; round++)
        {
            for (int ci = 0; ci < _configs.Count; ci++)
            {
                if (_budget.Expired || _budget.Used >= 0.45) return iterations;
                if (round >= perConfig[ci].Count) continue;
                var (o, s, f, r) = perConfig[ci][round];
                var c = _configs[ci];
                var (tiers, open) = Strategies(c)[s];
                var pol = new PlacementPolicy(tiers, open, FitSplits[f].Fit, FitSplits[f].Split, RandomTies: false, Rows: r == 1);
                var order = PieceOrdering.Build(_p, all, PieceOrdering.Deterministic[o], SourcesOf(c), null, 0);
                ConstructAndSubmit(c, order, pol, $"construct:{c.Name}:{PieceOrdering.Deterministic[o]}:{pol}");
                iterations++;
            }
        }

        // Seeded random restarts until the phase share is used or they stop paying off.
        int stale = 0;
        while (!_budget.Expired && _budget.Used < 0.45 && stale < Math.Max(50, _opt.StagnationLimit / 3))
        {
            var c = _configs[_rng.Next(_configs.Count)];
            var kind = _rng.NextDouble() < 0.25 ? OrderKind.Random : PieceOrdering.Deterministic[_rng.Next(PieceOrdering.Deterministic.Length)];
            var order = PieceOrdering.Build(_p, all, kind, SourcesOf(c), _rng, 0.05 + 0.3 * _rng.NextDouble());
            var pol = RandomPolicy(c.HasRoll);
            bool improved = ConstructAndSubmit(c, order, pol, $"restart:{c.Name}:{kind}:{pol}");
            stale = improved ? 0 : stale + 1;
            iterations++;
        }

        return iterations;
    }

    private bool ConstructAndSubmit(SourceConfig c, int[] order, PlacementPolicy pol, string origin)
    {
        Constructions++;
        _budget.Spend();
        var st = PackingState.Create(_p, c.Sources, c.Name);
        st.Origin = origin;
        if (!Construction.PlaceAll(st, order, pol, _rng)) return false;
        return Submit(st).NewBest;
    }

    private PlacementPolicy RandomPolicy(bool hasRoll)
    {
        double u = _rng.NextDouble();
        var tiers = !hasRoll
            ? (u < 0.5 ? TierOrder.OpenedFirst : u < 0.8 ? TierOrder.FitFirst : TierOrder.MinExtension)
            : (u < 0.5 ? TierOrder.MinExtension : u < 0.7 ? TierOrder.RemnantsFirst : u < 0.85 ? TierOrder.FitFirst : TierOrder.OpenedFirst);
        return new PlacementPolicy(tiers, (OpenPreference)_rng.Next(3), (FitRule)_rng.Next(4), (SplitRule)_rng.Next(6), RandomTies: _rng.Next(2) == 0,
            Rows: hasRoll && _rng.Next(2) == 0);
    }

    // ---- phase 2: beam search ----------------------------------------------------------------------------------
    private int RunBeam()
    {
        if (_p.Instances.Length > 400 || _opt.BeamWidth < 2) return 0;
        int runs = 0;
        var all = Enumerable.Range(0, _p.Instances.Length).ToArray();
        // Most promising configurations first (by their best verified plan), then the rest.
        var ranked = Pool.Sorted();
        var configOrder = _configs
            .OrderBy(c =>
            {
                int i = 0;
                foreach (var e in ranked)
                {
                    if (e.State.Config == c.Name) return i;
                    i++;
                }

                return int.MaxValue;
            })
            .ToList();
        foreach (var c in configOrder)
        {
            foreach (var kind in new[] { OrderKind.AreaDesc, OrderKind.MaxSideDesc, OrderKind.ConstrainedFirst })
            {
                if (_budget.Expired || _budget.Used >= 0.6) return runs;
                var (tiers, open) = Strategies(c)[0];
                var pol = new PlacementPolicy(tiers, open, FitRule.BestAreaFit, SplitRule.MaximizeLargerResidual, RandomTies: false);
                var order = PieceOrdering.Build(_p, all, kind, SourcesOf(c), null, 0);
                var start = PackingState.Create(_p, c.Sources, c.Name);
                start.Origin = $"beam:{c.Name}:{kind}:w{_opt.BeamWidth}";
                _budget.Spend(_opt.BeamWidth);
                BeamRuns++;
                runs++;
                var result = Construction.Beam(start, order, pol, _opt.BeamWidth, Math.Max(1, _opt.BeamBranching), _rng, _budget);
                if (result is not null)
                {
                    result.Origin = start.Origin;
                    Submit(result);
                }
            }
        }

        return runs;
    }

    // ---- phase 3: destroy-and-repair improvement ----------------------------------------------------------------
    private bool RunImprovement()
    {
        if (Pool.Count == 0) return false;
        var current = Pool.Best!;
        int stagnation = 0;
        var ops = Enum.GetValues<DestroyOp>();
        while (!_budget.Expired)
        {
            if (stagnation >= _opt.StagnationLimit) return true;
            ImprovementIterations++;
            _budget.Spend();

            double u = _rng.NextDouble();
            PoolEntry baseEntry;
            if (u < 0.55) baseEntry = current;
            else if (u < 0.75) baseEntry = Pool.Best!;
            else if (u < 0.9 && Pool.GroupBests.Count > 0) baseEntry = Pool.GroupBests.Values.OrderBy(e => e.Serial).ElementAt(_rng.Next(Pool.GroupBests.Count));
            else
            {
                var top = Pool.Sorted();
                baseEntry = top[_rng.Next(Math.Min(8, top.Count))];
            }

            var st = baseEntry.State.Clone();
            foreach (var l in st.Layouts) l.Enabled = true;
            var op = ops[_rng.Next(ops.Length)];
            var removed = Destroy(st, op);
            if (removed.Count == 0)
            {
                stagnation++;
                continue;
            }

            st.RefreshDirty();
            var kind = _rng.NextDouble() < 0.3 ? OrderKind.Random : PieceOrdering.Deterministic[_rng.Next(PieceOrdering.Deterministic.Length)];
            var order = PieceOrdering.Build(_p, removed, kind, st.Layouts.Select(l => l.Spec).ToList(), _rng, 0.15 * _rng.NextDouble());
            var pol = RandomPolicy(st.Layouts.Any(l => l.IsRoll && l.Enabled));
            st.Origin = $"improve:{op}:{pol}";
            if (!Construction.PlaceAll(st, order, pol, _rng))
            {
                stagnation++;
                continue;
            }

            // Cheap pre-filter: a repair that buys more roll than its base cannot improve tier 1; skip the
            // expensive finalise/assemble/validate path for it.
            if (QuickPurchasedArea(st) > QuickPurchasedArea(baseEntry.State))
            {
                stagnation++;
                continue;
            }

            var (entry, newBest) = Submit(st);
            if (newBest)
            {
                stagnation = 0;
                current = Pool.Best!;
            }
            else
            {
                stagnation++;
                if (entry is not null && Pool.Comparer.Compare(entry.Plan, current.Plan) <= 0) current = entry;
            }
        }

        return false;
    }

    private static long QuickPurchasedArea(PackingState st)
    {
        st.RefreshDirty();
        foreach (var l in st.Layouts)
            if (l.IsRoll && l.PieceCount > 0)
                return NormalizedProblem.OrderedLength(l.Spec, l.Consumed) is long ordered ? l.Spec.W * ordered : long.MaxValue;
        return 0;
    }

    private List<int> Destroy(PackingState st, DestroyOp op)
    {
        int n = _p.Instances.Length;
        var removed = new List<int>();
        void Take(IEnumerable<int> instances)
        {
            foreach (int i in instances.ToList())
            {
                if (st.NodeOf[i] is null) continue;
                st.Remove(i);
                removed.Add(i);
            }
        }

        int roll = st.RollLayout;
        var used = Enumerable.Range(0, st.Layouts.Length).Where(li => st.Layouts[li].PieceCount > 0).ToList();
        switch (op)
        {
            case DestroyOp.RollTail when roll >= 0 && st.Layouts[roll].PieceCount > 0:
            {
                long c = st.Layouts[roll].Consumed;
                long delta = (long)(c * (0.1 + 0.35 * _rng.NextDouble()));
                Take(st.InstancesIn(roll).Where(i => st.NodeOf[i]!.R.Bottom > c - delta));
                break;
            }
            case DestroyOp.RebuildSource when used.Count > 0:
                Take(st.InstancesIn(used[_rng.Next(used.Count)]));
                break;
            case DestroyOp.Window:
            {
                int pivot = _rng.Next(n);
                var pn = st.NodeOf[pivot]!;
                int li = st.LayoutOf[pivot];
                long span = Math.Max(pn.R.W, pn.R.H) * (1 + _rng.Next(3));
                var win = new Rect(pn.R.X - span / 2, pn.R.Y - span / 2, pn.R.W + span, pn.R.H + span);
                Take(st.InstancesIn(li).Where(i =>
                {
                    var r = st.NodeOf[i]!.R;
                    return r.X < win.Right && win.X < r.Right && r.Y < win.Bottom && win.Y < r.Bottom;
                }));
                break;
            }
            case DestroyOp.EmptyRoll when roll >= 0 && st.Layouts[roll].PieceCount > 0 && st.Layouts.Length > 1:
                Take(st.InstancesIn(roll));
                st.Layouts[roll].Enabled = false;
                break;
            case DestroyOp.DropRemnant when used.Any(li => !st.Layouts[li].IsRoll):
            {
                var remnants = used.Where(li => !st.Layouts[li].IsRoll).ToList();
                int li = remnants[_rng.Next(remnants.Count)];
                Take(st.InstancesIn(li));
                st.Layouts[li].Enabled = false;
                break;
            }
            default:
            {
                int k = 1 + _rng.Next(Math.Max(1, Math.Min(n, 2 + n / 5)));
                var pick = Enumerable.Range(0, n).ToList();
                _rng.Shuffle(pick);
                Take(pick.Take(k));
                break;
            }
        }

        return removed;
    }
}

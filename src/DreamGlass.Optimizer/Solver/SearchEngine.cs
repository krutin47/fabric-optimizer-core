using System.Diagnostics;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Exact;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Solver;

/// <summary>A choice of physical sources to search over: warehouse only, joint stock + one roll offer, or roll only.</summary>
internal sealed class SourceConfig(string name, int[] sources, bool hasRoll, bool hasRemnants, long boundArea, decimal boundCost)
{
    public string Name { get; } = name;
    public int[] Sources { get; } = sources;
    public bool HasRoll { get; } = hasRoll;
    public bool HasRemnants { get; } = hasRemnants;

    /// <summary>Least area and cost any plan of this configuration must buy (a relaxation; 0 for warehouse-only).</summary>
    public long BoundArea { get; } = boundArea;
    public decimal BoundCost { get; } = boundCost;

    public int Attempts { get; set; }
    public int Completes { get; set; }

    /// <summary>Enough tries without a single complete layout: stop spending the budget here.</summary>
    public bool GaveUp => Completes == 0 && Attempts >= 12;
}

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
/// Hybrid time-bounded search (spec §6, handbook §33): exact small-case DP → deterministic multi-start construction
/// over source configurations, orderings, assignment strategies, fit and split rules → seeded random restarts → beam
/// search → destroy-and-repair improvement.
/// <para>
/// Every finished layout is reduced to its metrics and a structural key (<see cref="PlanAssembler.Digest"/>).
/// Layouts already seen, and layouts that could not earn a place among the kept ones, stop there. A kept layout is
/// built into a full plan and passed through the independent validator before it can be returned
/// (<see cref="Materialize(PoolEntry)"/>): at the final selection, or as soon as it is kept when
/// <see cref="OptimizationOptions.ValidateEveryKeptPlan"/> is set. Nothing unvalidated ever leaves the engine.
/// </para>
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

    /// <summary>Structural keys remembered for duplicate detection; cleared when full (correctness never depends on it).</summary>
    private const int MaxSeenLayouts = 400_000;
    private const int MaxRejectedKept = 10;

    private readonly NormalizedProblem _p;
    private readonly OptimizationOptions _opt;
    private readonly SearchBudget _budget;
    private readonly Rng _rng;
    private readonly CutPlanValidator _validator = new();
    private readonly List<SourceConfig> _configs = [];
    private readonly HashSet<PlanKey> _seen = [];

    public CandidatePool Pool { get; }
    public int Constructions { get; private set; }
    public int CompletePlans { get; private set; }
    public int Validated { get; private set; }
    public int Rejected { get; private set; }
    public int DuplicatesSkipped { get; private set; }
    public int UncompetitiveSkipped { get; private set; }
    public int BeamRuns { get; private set; }
    public int ImprovementIterations { get; private set; }
    public List<string> RejectionSamples { get; } = [];
    public List<RejectedCandidate> RejectedCandidates { get; } = [];
    public List<PhaseTiming> Phases { get; } = [];
    public ExactOutcome? Exact { get; private set; }
    public StopReason Stop { get; private set; } = StopReason.NotStarted;

    /// <summary>Stopwatch ticks spent building layouts, turning them into plans, and validating them.</summary>
    public long ConstructTicks { get; private set; }
    public long AssembleTicks { get; private set; }
    public long ValidateTicks { get; private set; }

    public SearchEngine(NormalizedProblem p, OptimizationOptions opt, SearchBudget budget)
    {
        _p = p;
        _opt = opt;
        _budget = budget;
        _rng = new Rng(p.Snapshot.Search.Seed);
        Pool = new CandidatePool(opt.Objective, opt.PoolCapacity);
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

    /// <summary>
    /// Source configurations worth searching. Left out: warehouse-only when the remnants are smaller than the job, and
    /// any roll offer that cannot hold the pieces that must lie on it (too narrow, or over the supplier's length).
    /// </summary>
    private void BuildConfigs()
    {
        var rem = _p.RemnantSources;
        if (rem.Length > 0 && _p.RequiredArea <= _p.TotalRemnantArea)
            _configs.Add(new SourceConfig("warehouse-only", rem, false, true, 0, 0m));
        var allPieces = _p.Instances.Select(i => _p.Types[i.Type]).ToList();
        var (forced, needJoint) = Bounds.ForcedOntoRoll(_p);
        foreach (int r in _p.RollSources)
        {
            var roll = _p.Sources[r];
            string offer = roll.Offer!.OfferId;
            if (rem.Length > 0 && Bounds.ForRoll(_p, roll, needJoint, forced) is var (ja, jc))
                _configs.Add(new SourceConfig($"joint:{offer}", [.. rem, r], true, true, ja, jc));
            if (Bounds.ForRoll(_p, roll, _p.RequiredArea, allPieces) is var (ra, rc))
                _configs.Add(new SourceConfig($"roll-only:{offer}", [r], true, false, ra, rc));
        }
    }

    /// <summary>
    /// True when no plan of this configuration can rank first: its cheapest possible purchase is already beaten on
    /// the ranking's first tier. Its plans from the deterministic round stay available as alternatives.
    /// </summary>
    private bool Dominated(SourceConfig c)
    {
        if (Pool.Best is not { } best) return false;
        return _opt.Objective.Tiers[0] switch
        {
            ObjectiveCriterion.PurchaseCost => c.BoundCost > best.Metrics.PurchaseCost,
            ObjectiveCriterion.PurchasedArea => Units.AreaToM2(c.BoundArea, _p.Scale) > best.Metrics.PurchasedAreaM2,
            _ => false,
        };
    }

    private List<SourceConfig> LiveConfigs()
    {
        var live = _configs.Where(c => !c.GaveUp && !Dominated(c)).ToList();
        return live.Count > 0 ? live : _configs.Where(c => !c.GaveUp).ToList();
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

    // ---- submission: finalise → digest → (duplicate? competitive?) → keep; validation before anything is returned -----

    /// <summary>
    /// Outcome of submitting a layout. <see cref="Metrics"/> is set for every complete, non-rejected layout, also when
    /// it was not kept, so the improvement phase can still walk through it.
    /// </summary>
    private readonly record struct Submission(PoolEntry? Entry, bool NewBest, PlanMetrics? Metrics);

    private Submission Submit(PackingState st)
    {
        long t0 = Stopwatch.GetTimestamp();
        var fp = Finalizer.Finalize(st);
        if (fp is null) return default;
        CompletePlans++;
        var digest = PlanAssembler.Digest(fp);
        if (_seen.Count >= MaxSeenLayouts) _seen.Clear();
        bool duplicate = !_seen.Add(digest.Key);
        bool kept = !duplicate && Pool.Admits(digest.Metrics, digest.Group);
        if (duplicate) DuplicatesSkipped++;
        else if (!kept) UncompetitiveSkipped++;
        AssembleTicks += Stopwatch.GetTimestamp() - t0;
        if (!kept) return new Submission(null, false, digest.Metrics);

        var entry = new PoolEntry { Digest = digest, State = st };
        if (_opt.ValidateEveryKeptPlan && !Materialize(entry, fp)) return default;
        var (added, newBest) = Pool.Add(entry);
        return new Submission(added ? entry : null, newBest, digest.Metrics);
    }

    /// <summary>
    /// Builds the full plan of a kept layout and passes it through the independent validator. Returns false, and
    /// records the structured reasons, when the validator rejects it (a generator defect: the plan is never used).
    /// </summary>
    public bool Materialize(PoolEntry entry) => Materialize(entry, null);

    private bool Materialize(PoolEntry entry, FinalPlan? fp)
    {
        if (entry.Plan is not null) return true;
        long t0 = Stopwatch.GetTimestamp();
        fp ??= Finalizer.Finalize(entry.State) ?? throw new InvalidOperationException("A kept layout could not be finalised again.");
        var plan = PlanAssembler.Assemble(fp, entry.Digest);
        long t1 = Stopwatch.GetTimestamp();
        AssembleTicks += t1 - t0;
        var outcome = _validator.Validate(_p.Snapshot, plan);
        ValidateTicks += Stopwatch.GetTimestamp() - t1;
        if (!outcome.Passed)
        {
            Rejected++;
            if (RejectionSamples.Count < 5)
                RejectionSamples.Add($"{entry.State.Origin}: " + string.Join("; ", outcome.Failures.Take(3).Select(f => $"{f.Code} {f.Message}")));
            if (RejectedCandidates.Count < MaxRejectedKept)
                RejectedCandidates.Add(new RejectedCandidate(plan.CandidateId, entry.State.Origin, outcome.Failures.Take(10).ToList()));
            return false;
        }

        Validated++;
        entry.Plan = plan with { Validation = outcome };
        return true;
    }

    // ---- phase 0: exact --------------------------------------------------------------------------------------
    private void RunExact()
    {
        long deadline = _opt.Mode == ExecutionMode.Deterministic
            ? _budget.TimeBudgetMs
            : _budget.ElapsedMs + (long)(_budget.TimeBudgetMs * Math.Clamp(_opt.Exact.TimeShare, 0, 1));
        Exact = ExactGuillotineSolver.TrySolve(_p, _opt.Exact, _budget, deadline);
        if (Exact.Plan is { } plan) Submit(plan);
        if (Exact.CostPlan is { } cheapest) Submit(cheapest);
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
                var c = _configs[ci];
                if (round >= perConfig[ci].Count || c.GaveUp) continue;
                var (o, s, f, r) = perConfig[ci][round];
                var (tiers, open) = Strategies(c)[s];
                var pol = new PlacementPolicy(tiers, open, FitSplits[f].Fit, FitSplits[f].Split, RandomTies: false, Rows: r == 1);
                var order = PieceOrdering.Build(_p, all, PieceOrdering.Deterministic[o], SourcesOf(c), null, 0);
                ConstructAndSubmit(c, order, pol, $"construct:{c.Name}:{PieceOrdering.Deterministic[o]}:{pol}");
                iterations++;
            }
        }

        // Seeded random restarts on the configurations that can still rank first, until the phase share is used or
        // they stop paying off.
        int stale = 0;
        while (!_budget.Expired && _budget.Used < 0.45 && stale < Math.Max(50, _opt.StagnationLimit / 3))
        {
            var live = LiveConfigs();
            if (live.Count == 0) break;
            var c = live[_rng.Next(live.Count)];
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
        c.Attempts++;
        _budget.Spend();
        var st = PackingState.Create(_p, c.Sources, c.Name);
        st.Origin = origin;
        long t0 = Stopwatch.GetTimestamp();
        bool placed = Construction.PlaceAll(st, order, pol, _rng);
        ConstructTicks += Stopwatch.GetTimestamp() - t0;
        if (!placed) return false;
        var sub = Submit(st);
        if (sub.Metrics is not null) c.Completes++;
        return sub.NewBest;
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
        // Most promising configurations first (by their best verified plan); configurations that never completed a
        // layout or cannot rank first are skipped.
        var ranked = Pool.Sorted();
        var configOrder = _configs
            .Where(c => c.Completes > 0 && !Dominated(c))
            .OrderBy(c =>
            {
                for (int i = 0; i < ranked.Count; i++)
                    if (ranked[i].State.Config == c.Name) return i;
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
                long t0 = Stopwatch.GetTimestamp();
                var result = Construction.Beam(start, order, pol, _opt.BeamWidth, Math.Max(1, _opt.BeamBranching), _rng, _budget);
                ConstructTicks += Stopwatch.GetTimestamp() - t0;
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
        if (Pool.Best is not { } first) return false;
        // The walk's current position. It need not be a kept plan: a layout that ties the best on every figure is a
        // fine place to continue from, and only what enters the pool has to be validated.
        (PackingState State, PlanMetrics Metrics) current = (first.State, first.Metrics);
        // A second walk guided by roll length. Fabric is bought in whole metres, so price only drops when a layout
        // crosses a metre boundary and the ranking gives no credit for getting closer to it. This walk keeps the
        // shortest layout at the best price found so far and tries to shorten it further.
        var shortest = current;
        int stagnation = 0;
        var ops = Enum.GetValues<DestroyOp>();
        while (!_budget.Expired)
        {
            if (stagnation >= _opt.StagnationLimit) return true;
            ImprovementIterations++;
            _budget.Spend();

            double u = _rng.NextDouble();
            PackingState baseState;
            if (u < 0.40) baseState = current.State;
            else if (u < 0.60) baseState = shortest.State;
            else if (u < 0.75) baseState = Pool.Best!.State;
            else if (u < 0.9 && Pool.GroupBests.Count > 0) baseState = Pool.GroupBests.Values.OrderBy(e => e.Serial).ElementAt(_rng.Next(Pool.GroupBests.Count)).State;
            else
            {
                var top = Pool.Sorted();
                baseState = top[_rng.Next(Math.Min(8, top.Count))].State;
            }

            long t0 = Stopwatch.GetTimestamp();
            var st = baseState.Clone();
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
            bool repaired = Construction.PlaceAll(st, order, pol, _rng);
            ConstructTicks += Stopwatch.GetTimestamp() - t0;
            if (!repaired)
            {
                stagnation++;
                continue;
            }

            // Cheap pre-filter: a repair that buys more roll than its base cannot improve on it; skip the
            // finalise/digest path for it.
            if (QuickPurchasedArea(st) > QuickPurchasedArea(baseState))
            {
                stagnation++;
                continue;
            }

            var sub = Submit(st);
            if (sub.Metrics is not { } m)
            {
                stagnation++;
                continue;
            }

            int guide = GuideCompare(m, shortest.Metrics);
            if (guide <= 0) shortest = (st, m);
            if (sub.NewBest)
            {
                stagnation = 0;
                current = (Pool.Best!.State, Pool.Best.Metrics);
            }
            else
            {
                // A strictly shorter layout at the best price is progress towards the next cheaper metre.
                stagnation = guide < 0 ? 0 : stagnation + 1;
                if (PlanRanking.Compare(m, current.Metrics, _opt.Objective) <= 0) current = (st, m);
            }
        }

        return false;
    }

    /// <summary>Search guidance only (never ranking): price, then roll length used, then the ranking itself.</summary>
    private int GuideCompare(PlanMetrics a, PlanMetrics b)
    {
        int c = a.PurchaseCost.CompareTo(b.PurchaseCost);
        if (c != 0) return c;
        c = (a.ConsumedLengthMm ?? 0).CompareTo(b.ConsumedLengthMm ?? 0);
        return c != 0 ? c : PlanRanking.Compare(a, b, _opt.Objective);
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
        // Rebuilding a whole source that holds most of a large job is a restart, not a repair: keep the destroyed
        // part to about a third of the job and use a spatial window instead.
        int limit = Math.Max(24, n / 3);
        int rebuild = -1;
        if (op == DestroyOp.RebuildSource && used.Count > 0)
        {
            rebuild = used[_rng.Next(used.Count)];
            if (st.Layouts[rebuild].PieceCount > limit) op = DestroyOp.Window;
        }
        else if (op == DestroyOp.EmptyRoll && roll >= 0 && st.Layouts[roll].PieceCount > limit)
        {
            op = DestroyOp.Window;
        }

        switch (op)
        {
            case DestroyOp.RollTail when roll >= 0 && st.Layouts[roll].PieceCount > 0:
            {
                long c = st.Layouts[roll].Consumed;
                long delta = (long)(c * (0.1 + 0.35 * _rng.NextDouble()));
                Take(st.InstancesIn(roll).Where(i => st.NodeOf[i]!.R.Bottom > c - delta));
                break;
            }
            case DestroyOp.RebuildSource when rebuild >= 0:
                Take(st.InstancesIn(rebuild));
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

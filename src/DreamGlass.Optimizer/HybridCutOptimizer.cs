using System.Diagnostics;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Solver;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer;

/// <summary>
/// DreamGlass hybrid guillotine optimiser. Contract (spec §0): return the best <b>verified</b> guillotine-cuttable
/// plans found within the search budget, with exact coordinates, ordered cuts, purchase quantity and residuals;
/// never claim an unproved global optimum.
/// </summary>
public sealed class HybridCutOptimizer : ICutOptimizer
{
    public const string SolverVersion = "dreamglass-hybrid/1.4.0";

    /// <summary>
    /// The DreamGlass build (package) version, e.g. "1.3.1". It goes up with every release, including releases that
    /// change only the printouts or the playground, so it tells which build is running; <see cref="SolverVersion"/>
    /// changes only when the search itself changes.
    /// </summary>
    public static string BuildVersion { get; } =
        (typeof(HybridCutOptimizer).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?")
        .Split('+')[0]; // the SDK may append "+<commit>"

    public Task<OptimizationResult> OptimizeAsync(OptimizationSnapshot input, OptimizationOptions options, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => Optimize(input, options, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>Synchronous entry point. Never throws: invalid input, timeouts, cancellation and faults become statuses.</summary>
    public OptimizationResult Optimize(OptimizationSnapshot input, OptimizationOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= OptimizationOptions.Default;
        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var envelope = new OptimizationResult
        {
            RunId = Guid.NewGuid().ToString("N"),
            InputHash = HashOrEmpty(() => OptimizerJson.Hash(new { input, options })),
            SnapshotHash = input is null ? "" : HashOrEmpty(() => OptimizerJson.SnapshotHash(input)),
            OrganizationId = input?.OrganizationId,
            JobId = input?.JobId,
            JobRevisionId = input?.JobRevisionId ?? "",
            StartedAtUtc = started,
            CompletedAtUtc = started,
            Runtime = Runtime,
            SchemaVersion = input?.SchemaVersion ?? 0,
            SolverVersion = SolverVersion,
            ObjectiveVersion = options.Objective?.Version ?? "",
            ObjectiveApproved = options.Objective?.Approved ?? false,
            ValidatorVersion = CutPlanValidator.Version,
            Seed = input?.Search?.Seed ?? 0,
            Mode = options.Mode,
            TimeBudgetMs = input?.Search?.TimeBudgetMs ?? 0,
        };
        OptimizationResult Done(OptimizationResult r) => r with { ElapsedMs = sw.ElapsedMilliseconds, CompletedAtUtc = DateTimeOffset.UtcNow };

        try
        {
            if (options.Objective is null || options.Objective.Tiers is null || options.Objective.Tiers.Count == 0)
            {
                return Done(envelope with
                {
                    Status = RunStatus.InvalidInput,
                    InputErrors = [new InputError("OBJECTIVE_MISSING", "options.objective", "No objective tiers configured.", "Use ObjectivePolicy.CheapestFirst or another configured policy.")],
                });
            }

            var check = InputValidator.Validate(input, options);
            if (!check.IsValid)
            {
                return Done(envelope with
                {
                    Status = RunStatus.InvalidInput,
                    InputErrors = check.Errors,
                    Diagnostics = new RunDiagnostics { Warnings = check.Warnings, Reproducible = true },
                });
            }

            var problem = Normalizer.Normalize(input!);
            var warnings = check.Warnings.ToList();
            if (!options.Objective.Approved)
                warnings.Add($"Objective '{options.Objective.Version}' is a proposal pending product-owner approval (ADR-015).");

            long workLimit = options.Mode == ExecutionMode.Deterministic ? Math.Max(1, options.WorkBudget ?? OptimizationOptions.DefaultWorkBudget) : long.MaxValue;
            var budget = new SearchBudget(input!.Search.TimeBudgetMs, workLimit, options.Mode, cancellationToken);

            var infeasible = new List<string>();
            if (problem.RollSources.Length == 0 && problem.RequiredArea > problem.TotalRemnantArea)
            {
                infeasible.Add($"Requested padded area {Units.AreaToM2(problem.RequiredArea, problem.Scale)} m² exceeds total remnant area {Units.AreaToM2(problem.TotalRemnantArea, problem.Scale)} m² and no roll offer was supplied.");
                return Done(envelope with
                {
                    Status = RunStatus.NoFeasiblePlan,
                    StopReason = StopReason.NotStarted,
                    Diagnostics = new RunDiagnostics { Warnings = warnings, InfeasibilityReasons = infeasible, Reproducible = true },
                });
            }

            var engine = new SearchEngine(problem, options, budget);
            engine.Run();

            var bounds = Bounds.Compute(problem, engine.Exact);
            var firstTier = options.Objective.Tiers[0];
            // Every plan picked here is built and independently validated before it is returned; rejected ones are dropped.
            var selection = engine.Pool.SelectFinal(Math.Max(1, input.Search.MaxCandidates), engine.Materialize);
            var candidates = new List<CandidateCutPlan>();
            for (int i = 0; i < selection.Count; i++)
            {
                var plan = selection[i].Entry.Plan!;
                ObjectiveBound? areaBound = null, costBound = null;
                if (bounds.AreaUnits is long b)
                {
                    decimal lbM2 = Units.AreaToM2(b, problem.Scale);
                    decimal gap = plan.Metrics.PurchasedAreaM2 - lbM2;
                    areaBound = new ObjectiveBound
                    {
                        Metric = "PurchasedAreaM2",
                        Scope = "Newly purchased area (m²) only. Price, scrap, leftover size and cuts are not bounded by this figure.",
                        Method = bounds.Method,
                        LowerBoundM2 = lbM2,
                        GapM2 = gap,
                        GapPercentOfBound = lbM2 > 0 ? Math.Round(100m * gap / lbM2, 2) : null,
                        Proven = bounds.Proven,
                        Note = BoundNote(bounds.Proven, gap == 0, "purchased area", "A smaller purchase exists"),
                    };
                }

                if (bounds.Cost is decimal cb)
                {
                    decimal gap = plan.Metrics.PurchaseCost - cb;
                    costBound = new ObjectiveBound
                    {
                        Metric = "PurchaseCost",
                        Scope = "Purchase cost only. Scrap, leftover size and cuts are not bounded by this figure.",
                        Method = bounds.Method,
                        LowerBoundM2 = cb,
                        GapM2 = gap,
                        GapPercentOfBound = cb > 0 ? Math.Round(100m * gap / cb, 2) : null,
                        Proven = bounds.Proven,
                        Note = BoundNote(bounds.Proven, gap == 0, "purchase cost", "A cheaper purchase exists"),
                    };
                }

                // "BoundedGap" speaks about the ranking's first tier only, and only when that tier has a bound.
                var tierBound = firstTier switch
                {
                    ObjectiveCriterion.PurchaseCost => costBound,
                    ObjectiveCriterion.PurchasedArea => areaBound,
                    _ => null,
                };
                var quality = tierBound is not null && (tierBound.Proven || tierBound.GapM2 == 0) ? QualityStatus.BoundedGap : QualityStatus.BestFound;

                var planWarnings = new List<string>();
                if (plan.Metrics.ReusableOffcutCount > 0)
                    planWarnings.Add("Predicted reusable offcuts are not inventory until physically confirmed after cutting.");
                if (problem.Policy is null) planWarnings.Add("No remnant policy: all residuals are reported as not eligible.");

                candidates.Add(plan with
                {
                    Rank = i + 1,
                    Tags = selection[i].Tags,
                    QualityStatus = quality,
                    Bound = areaBound,
                    CostBound = costBound,
                    Warnings = planWarnings,
                });
            }

            var stop = engine.Stop;
            RunStatus status;
            if (candidates.Count > 0) status = RunStatus.Succeeded;
            else if (engine.Exact is { Completed: true, ProvenInfeasible: true })
            {
                status = RunStatus.NoFeasiblePlan;
                infeasible.Add(engine.Exact.Reason);
            }
            else if (stop == StopReason.Cancelled) status = RunStatus.Cancelled;
            else if (stop == StopReason.TimeBudget) status = RunStatus.TimedOutWithoutPlan;
            else
            {
                status = RunStatus.NoPlanFound;
                infeasible.Add("The search schedule finished without a valid plan; infeasibility is not proven. " +
                               (problem.RollSources.Length == 0 ? "Consider adding a roll offer." : "Check supplier length limits."));
            }

            var exact = engine.Exact;
            return Done(envelope with
            {
                Status = status,
                StopReason = stop,
                Candidates = candidates,
                ResultHash = OptimizerJson.Hash(candidates),
                Diagnostics = new RunDiagnostics
                {
                    Warnings = warnings,
                    InfeasibilityReasons = infeasible,
                    ConstructionsAttempted = engine.Constructions,
                    CompletePlansBuilt = engine.CompletePlans,
                    PlansValidated = engine.Validated,
                    PlansRejectedByValidator = engine.Rejected,
                    DuplicateLayoutsSkipped = engine.DuplicatesSkipped,
                    UncompetitiveLayoutsSkipped = engine.UncompetitiveSkipped,
                    BeamRuns = engine.BeamRuns,
                    ImprovementIterations = engine.ImprovementIterations,
                    PoolSize = engine.Pool.Count,
                    WorkUnitsUsed = budget.Work,
                    Reproducible = options.Mode == ExecutionMode.Deterministic && !budget.SafetyCapHit,
                    Phases = engine.Phases,
                    ConstructionMs = engine.ConstructTicks * 1000 / Stopwatch.Frequency,
                    AssemblyMs = engine.AssembleTicks * 1000 / Stopwatch.Frequency,
                    ValidationMs = engine.ValidateTicks * 1000 / Stopwatch.Frequency,
                    RejectionSamples = engine.RejectionSamples,
                    RejectedCandidates = engine.RejectedCandidates,
                    PurchasedAreaLowerBoundM2 = bounds.AreaUnits is long l ? Units.AreaToM2(l, problem.Scale) : null,
                    PurchasedAreaLowerBoundMethod = bounds.AreaUnits is null ? null : bounds.Method,
                    PurchaseCostLowerBound = bounds.Cost,
                    PurchaseCostLowerBoundMethod = bounds.Cost is null ? null : bounds.Method,
                    Exact = exact is null ? null : new ExactSolveReport
                    {
                        Attempted = exact.Attempted,
                        Completed = exact.Completed,
                        Reason = exact.Reason,
                        StatesExplored = exact.States,
                        WorkUnits = exact.Work,
                        ElapsedMs = exact.ElapsedMs,
                        ProvenMinPurchasedAreaM2 = exact.MinPurchasedAreaUnits is long m ? Units.AreaToM2(m, problem.Scale) : null,
                        ProvenMinPurchaseCost = exact.MinPurchaseCost,
                        ProvenInfeasible = exact.Completed ? exact.ProvenInfeasible : null,
                    },
                },
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A fault in the search is an explicit failed run with its reason, never a partial or "successful" result.
            return Done(envelope with
            {
                Status = RunStatus.Failed,
                Candidates = [],
                Diagnostics = new RunDiagnostics { FailureMessage = $"{ex.GetType().Name}: {ex.Message}" },
            });
        }
    }

    private static string BoundNote(bool proven, bool attained, string what, string betterExists) => proven
        ? (attained ? $"The {what} is proven minimal by exhaustive guillotine search." : $"{betterExists} (proven by exhaustive search); this candidate is shown for another trade-off.")
        : (attained ? $"The {what} meets the relaxation bound, hence is minimal." : "Relaxation bound only; the true optimum may lie anywhere in the gap.");

    private static RuntimeContext Runtime { get; } = new()
    {
        Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        ProcessorCount = Environment.ProcessorCount,
    };

    /// <summary>Hashing a malformed snapshot (for example a null list) must not prevent the structured input errors from being returned.</summary>
    private static string HashOrEmpty(Func<string> hash)
    {
        try
        {
            return hash();
        }
        catch (Exception ex) when (ex is NullReferenceException or NotSupportedException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return "";
        }
    }
}

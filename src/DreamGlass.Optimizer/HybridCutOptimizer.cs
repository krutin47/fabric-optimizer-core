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
    public const string SolverVersion = "dreamglass-hybrid/1.3.0";

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
        var envelope = new OptimizationResult
        {
            RunId = Guid.NewGuid().ToString("N"),
            InputHash = SafeHash(input, options),
            SchemaVersion = input?.SchemaVersion ?? 0,
            SolverVersion = SolverVersion,
            ObjectiveVersion = options.Objective?.Version ?? "",
            ObjectiveApproved = options.Objective?.Approved ?? false,
            ValidatorVersion = CutPlanValidator.Version,
            Seed = input?.Search?.Seed ?? 0,
            Mode = options.Mode,
            TimeBudgetMs = input?.Search?.TimeBudgetMs ?? 0,
        };

        try
        {
            if (options.Objective is null || options.Objective.Tiers is null || options.Objective.Tiers.Count == 0)
            {
                return envelope with
                {
                    Status = RunStatus.InvalidInput,
                    InputErrors = [new InputError("OBJECTIVE_MISSING", "options.objective", "No objective tiers configured.", "Use ObjectivePolicy.CheapestFirst or another configured policy.")],
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }

            var check = InputValidator.Validate(input, options);
            if (!check.IsValid)
            {
                return envelope with
                {
                    Status = RunStatus.InvalidInput,
                    InputErrors = check.Errors,
                    Diagnostics = new RunDiagnostics { Warnings = check.Warnings, Reproducible = true },
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }

            var problem = Normalizer.Normalize(input!);
            var warnings = check.Warnings.ToList();
            if (!options.Objective.Approved)
                warnings.Add($"Objective '{options.Objective.Version}' is a proposal pending product-owner approval (spec §12.1).");

            long workLimit = options.Mode == ExecutionMode.Deterministic ? Math.Max(1, options.WorkBudget ?? OptimizationOptions.DefaultWorkBudget) : long.MaxValue;
            var budget = new SearchBudget(input!.Search.TimeBudgetMs, workLimit, options.Mode, cancellationToken);

            var infeasible = new List<string>();
            if (problem.RollSources.Length == 0 && problem.RequiredArea > problem.TotalRemnantArea)
            {
                infeasible.Add($"Requested padded area {Units.AreaToM2(problem.RequiredArea, problem.Scale)} m² exceeds total remnant area {Units.AreaToM2(problem.TotalRemnantArea, problem.Scale)} m² and no roll offer was supplied.");
                return envelope with
                {
                    Status = RunStatus.NoFeasiblePlan,
                    StopReason = StopReason.NotStarted,
                    Diagnostics = new RunDiagnostics { Warnings = warnings, InfeasibilityReasons = infeasible, Reproducible = true },
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }

            var engine = new SearchEngine(problem, options, budget);
            engine.Run();

            var (lb, lbMethod) = Bounds.PurchasedArea(problem, engine.Exact);
            bool exactProof = lbMethod == "ExactGuillotineDP";
            var selection = engine.Pool.SelectFinal(Math.Max(1, input.Search.MaxCandidates));
            var candidates = new List<CandidateCutPlan>();
            for (int i = 0; i < selection.Count; i++)
            {
                var plan = selection[i].Entry.Plan;
                ObjectiveBound? bound = null;
                var quality = QualityStatus.BestFound;
                if (lb is long b)
                {
                    decimal lbM2 = Units.AreaToM2(b, problem.Scale);
                    decimal gap = plan.Metrics.PurchasedAreaM2 - lbM2;
                    bound = new ObjectiveBound
                    {
                        Scope = "Tier 1 only: newly purchased area (m²). Lower tiers (scrap, remnant quality, cost, cuts) are not bounded.",
                        Method = lbMethod,
                        LowerBoundM2 = lbM2,
                        GapM2 = gap,
                        Note = exactProof
                            ? (gap == 0 ? "Purchased area is proven minimal by exhaustive guillotine search." : "Exact search proved a smaller purchase exists; this candidate is shown for another trade-off.")
                            : (gap == 0 ? "Purchased area meets the relaxation bound, hence is minimal." : "Relaxation bound only; the true optimum may lie anywhere in the gap."),
                    };
                    if (exactProof || gap == 0) quality = QualityStatus.BoundedGap;
                }

                var planWarnings = new List<string>();
                if (plan.Metrics.ReusableOffcutCount > 0)
                    planWarnings.Add("Predicted reusable offcuts are not inventory until physically confirmed after cutting.");
                if (problem.Policy is null) planWarnings.Add("No remnant policy: all residuals are reported as not eligible.");

                candidates.Add(plan with
                {
                    Rank = i + 1,
                    Tags = selection[i].Tags,
                    QualityStatus = quality,
                    Bound = bound,
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
            return envelope with
            {
                Status = status,
                StopReason = stop,
                Candidates = candidates,
                ResultHash = OptimizerJson.Hash(candidates),
                ElapsedMs = sw.ElapsedMilliseconds,
                Diagnostics = new RunDiagnostics
                {
                    Warnings = warnings,
                    InfeasibilityReasons = infeasible,
                    ConstructionsAttempted = engine.Constructions,
                    CompletePlansBuilt = engine.CompletePlans,
                    PlansValidated = engine.Validated,
                    PlansRejectedByValidator = engine.Rejected,
                    BeamRuns = engine.BeamRuns,
                    ImprovementIterations = engine.ImprovementIterations,
                    PoolSize = engine.Pool.Count,
                    WorkUnitsUsed = budget.Work,
                    Reproducible = options.Mode == ExecutionMode.Deterministic && !budget.SafetyCapHit,
                    Phases = engine.Phases,
                    RejectionSamples = engine.RejectionSamples,
                    PurchasedAreaLowerBoundM2 = lb is long l ? Units.AreaToM2(l, problem.Scale) : null,
                    PurchasedAreaLowerBoundMethod = lb is null ? null : lbMethod,
                    Exact = exact is null ? null : new ExactSolveReport
                    {
                        Attempted = exact.Attempted,
                        Completed = exact.Completed,
                        Reason = exact.Reason,
                        StatesExplored = exact.States,
                        ElapsedMs = exact.ElapsedMs,
                        ProvenMinPurchasedAreaM2 = exact.MinPurchasedAreaUnits is long m ? Units.AreaToM2(m, problem.Scale) : null,
                        ProvenInfeasible = exact.Completed ? exact.ProvenInfeasible : null,
                    },
                },
            };
        }
        catch (Exception ex)
        {
            // Captured as an explicit failed run; nothing partial is returned.
            return envelope with
            {
                Status = RunStatus.Failed,
                ElapsedMs = sw.ElapsedMilliseconds,
                Candidates = [],
                Diagnostics = new RunDiagnostics { FailureMessage = $"{ex.GetType().Name}: {ex.Message}" },
            };
        }
    }

    private static string SafeHash(OptimizationSnapshot? input, OptimizationOptions options)
    {
        try
        {
            return OptimizerJson.Hash(new { input, options });
        }
        catch
        {
            return "";
        }
    }
}

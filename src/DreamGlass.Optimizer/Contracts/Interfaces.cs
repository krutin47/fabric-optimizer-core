namespace DreamGlass.Optimizer.Contracts;

/// <summary>Pure, versioned optimiser boundary (spec §11). No database or HTTP dependencies.</summary>
public interface ICutOptimizer
{
    /// <summary>
    /// Runs the search. CPU-bound: the returned task executes on a dedicated long-running thread; host it in a
    /// worker process. Never throws for bad input, timeouts or internal errors — those become explicit statuses.
    /// </summary>
    Task<OptimizationResult> OptimizeAsync(
        OptimizationSnapshot input,
        OptimizationOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Independent validator (spec §7). Treats the candidate as untrusted.</summary>
public interface ICutPlanValidator
{
    PlanValidationOutcome Validate(OptimizationSnapshot input, CandidateCutPlan candidate);
}

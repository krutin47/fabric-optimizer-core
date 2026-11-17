namespace DreamGlass.Optimizer.Contracts;

/// <summary>
/// Required pattern direction of the <b>finished panel</b>, relative to the panel's own reference
/// frame (its width axis is "horizontal", its length axis is "vertical") — never relative to the screen.
/// </summary>
public enum PatternOrientation
{
    /// <summary>No pattern obligation; the piece may be cut either way (it must still fit physically).</summary>
    None,
    /// <summary>The material's pattern axis must run parallel to the panel's length axis.</summary>
    Vertical,
    /// <summary>The material's pattern axis must run parallel to the panel's width axis.</summary>
    Horizontal,
}

/// <summary>Intrinsic pattern/grain axis of a source, expressed in the source's canonical (unrotated) coordinates.</summary>
public enum PatternAxis
{
    /// <summary>Pattern runs along the canonical length (y) axis — the normal case for a roll.</summary>
    Length,
    /// <summary>Pattern runs along the canonical width (x) axis.</summary>
    Width,
}

public enum SourceKind
{
    WarehouseRemnant,
    NewRoll,
}

/// <summary>
/// Direction of a guillotine cut line. <see cref="Vertical"/> = the line <c>x = position</c>, spanning the full
/// region length and producing left/right children. <see cref="Horizontal"/> = the line <c>y = position</c>,
/// spanning the full region width and producing top/bottom children.
/// </summary>
public enum CutAxis
{
    Vertical,
    Horizontal,
}

public enum CutNodeKind
{
    Cut,
    Piece,
    Residual,
}

public enum RunStatus
{
    /// <summary>At least one independently validated candidate is returned (see <see cref="StopReason"/>).</summary>
    Succeeded,
    /// <summary>Input failed validation; no search was run.</summary>
    InvalidInput,
    /// <summary>Infeasibility was <b>proven</b> (area argument or completed exact search).</summary>
    NoFeasiblePlan,
    /// <summary>The search schedule finished without a plan and without an infeasibility proof.</summary>
    NoPlanFound,
    /// <summary>The time budget expired before any valid plan was found.</summary>
    TimedOutWithoutPlan,
    /// <summary>Cancellation was requested before any valid plan was found.</summary>
    Cancelled,
    /// <summary>The run failed with an unexpected error (captured, never thrown to the caller).</summary>
    Failed,
}

public enum StopReason
{
    NotStarted,
    ScheduleExhausted,
    Converged,
    TimeBudget,
    WorkBudget,
    Cancelled,
}

public enum QualityStatus
{
    /// <summary>Strongest verified candidate observed before the budget expired. No optimality claim.</summary>
    BestFound,
    /// <summary>A valid bound exists for the stated objective scope; see the candidate's <c>Bound</c>.</summary>
    BoundedGap,
    /// <summary>Reserved: requires an exact proof of the <b>complete</b> lexicographic objective. Not emitted in v1.</summary>
    OptimalProven,
}

public enum ExecutionMode
{
    /// <summary>Search until the wall-clock budget, convergence or cancellation. Not bit-reproducible.</summary>
    TimeBudgeted,
    /// <summary>Search is bounded by a work-unit budget; identical input + seed + versions give identical output.
    /// The time budget remains a safety cap (if it triggers, the result is flagged non-reproducible).</summary>
    Deterministic,
}

/// <summary>Criteria available to the lexicographic objective comparator.</summary>
public enum ObjectiveCriterion
{
    /// <summary>Newly ordered roll area (minimise).</summary>
    PurchasedArea,
    /// <summary>Residual area not eligible as a reusable remnant (minimise).</summary>
    UnusableScrapArea,
    /// <summary>Area of the largest eligible reusable remnant (maximise).</summary>
    LargestReusableRemnantArea,
    /// <summary>Residual leaves narrower than the remnant policy's minimum width (minimise).</summary>
    ThinFragmentCount,
    /// <summary>Purchase cost from snapshotted prices (minimise).</summary>
    PurchaseCost,
    /// <summary>Number of guillotine cuts (minimise).</summary>
    TotalCuts,
    /// <summary>All residual area, reusable or not (minimise) — the historical "waste-first" reading.</summary>
    TotalResidualArea,
    /// <summary>Area of pre-existing warehouse sources committed by the plan (minimise).</summary>
    ExistingStockAreaConsumed,
    /// <summary>Total reusable residual area (maximise).</summary>
    ReusableResidualArea,
    /// <summary>Number of physical sources touched (minimise).</summary>
    SourceCount,
    /// <summary>Running length of the new roll the layout uses, padding included (minimise; 0 when nothing is ordered).</summary>
    RollLengthUsed,
}

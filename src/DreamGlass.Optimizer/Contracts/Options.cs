namespace DreamGlass.Optimizer.Contracts;

/// <summary>Engine-level configuration that is not part of the business snapshot.</summary>
public sealed record OptimizationOptions
{
    public static OptimizationOptions Default { get; } = new();

    public ExecutionMode Mode { get; init; } = ExecutionMode.TimeBudgeted;

    /// <summary>
    /// Work units for <see cref="ExecutionMode.Deterministic"/> (one unit ≈ one construction or repair).
    /// Ignored in time-budgeted mode. Defaults to <see cref="DefaultWorkBudget"/>.
    /// </summary>
    public int? WorkBudget { get; init; }

    public const int DefaultWorkBudget = 2500;

    public ObjectivePolicy Objective { get; init; } = ObjectivePolicy.CheapestFirst;

    public ExactSolverOptions Exact { get; init; } = new();

    public int BeamWidth { get; init; } = 6;
    public int BeamBranching { get; init; } = 3;

    /// <summary>
    /// Stop the improvement phase after this many consecutive iterations without progress (a new best plan, or a
    /// shorter layout at the best price).
    /// </summary>
    public int StagnationLimit { get; init; } = 8000;

    /// <summary>
    /// Number of verified plans the search keeps (plus the best per roll offer and the trade-off extremes). A layout
    /// is only built into a full plan and validated when it could earn one of these places, so a smaller pool means
    /// more of the budget goes into finding layouts. 32 measured better than 120 on every job size (docs/testing).
    /// </summary>
    public int PoolCapacity { get; init; } = 32;

    public OptimizerLimits Limits { get; init; } = new();

    /// <summary>
    /// Every returned candidate is always built and independently validated. With this flag the same is done for
    /// every layout the search keeps, the moment it is kept, so a generator defect surfaces during the search rather
    /// than at the end. It costs search time on large jobs; the test suite turns it on.
    /// </summary>
    public bool ValidateEveryKeptPlan { get; init; }
}

public sealed record ExactSolverOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Only attempt the exact solver when the job has at most this many piece instances. What really limits it is
    /// the number of distinct piece multisets (<see cref="MaxMultisets"/>): 40 copies of two piece types are easy,
    /// 16 different pieces are not.
    /// </summary>
    public int MaxInstances { get; init; } = 40;

    /// <summary>Upper bound on distinct piece multisets (product of (quantity+1) over piece types).</summary>
    public int MaxMultisets { get; init; } = 4096;

    public int MaxRemnants { get; init; } = 6;

    /// <summary>Memo-state cap; exceeding it aborts the exact run without any optimality claim.</summary>
    public int MaxStates { get; init; } = 400_000;

    /// <summary>
    /// Cap on splits examined. Unlike the time slice it does not depend on the machine, so a deterministic run stops
    /// at the same point everywhere. Exceeding it aborts the exact run without any optimality claim.
    /// </summary>
    public long MaxWork { get; init; } = 40_000_000;

    /// <summary>Fraction of the time budget the exact solver may use.</summary>
    public double TimeShare { get; init; } = 0.3;
}

/// <summary>Computational limits enforced before enqueueing (spec §3.6).</summary>
public sealed record OptimizerLimits
{
    public int MaxPieceTypes { get; init; } = 500;
    public int MaxPieceInstances { get; init; } = 2000;
    public int MaxQuantityPerPiece { get; init; } = 1000;
    public int MaxStockItems { get; init; } = 200;
    public int MaxRollOffers { get; init; } = 20;

    /// <summary>Largest permitted single dimension in scaled integer units (1e8 = 10 km at 0.1 mm).</summary>
    public long MaxDimensionUnits { get; init; } = 100_000_000;

    /// <summary>Largest permitted area aggregate in squared units; keeps all solver arithmetic inside Int64.</summary>
    public long MaxAggregateAreaUnits { get; init; } = 1_000_000_000_000_000_000;

    public int MaxTimeBudgetMs { get; init; } = 600_000;
    public int MaxCandidates { get; init; } = 20;
}

/// <summary>
/// Lexicographic objective. The default is <see cref="CheapestFirst"/>, the order the product owner approved on
/// 12 Nov 2026 (spec §12.1 decision): price, then least scrap, then the biggest keepable leftover.
/// <see cref="ProposedV1"/> (the spec §5 proposal) and <see cref="HistoricalWasteFirst"/> stay available for comparison.
/// </summary>
public sealed record ObjectivePolicy
{
    public string Version { get; init; } = "proposed-v1";

    /// <summary>True only once the product owner has signed off this exact tier order.</summary>
    public bool Approved { get; init; }

    public IReadOnlyList<ObjectiveCriterion> Tiers { get; init; } = [];

    /// <summary>
    /// Purchased areas are compared in buckets of this size (m²) so that "near-equivalent acquisition amounts"
    /// fall through to the next tier. 0 = exact comparison.
    /// </summary>
    public decimal PurchasedAreaTieToleranceM2 { get; init; }

    /// <summary>
    /// Unusable scrap is compared in buckets of this size (m²), so a tiny scrap difference cannot outrank a much
    /// better keepable leftover in the next tier. 0 = exact comparison. Buckets keep the comparator transitive; the
    /// price is that two values either side of a bucket edge still differ (0.0999 vs 0.1001 at 0.01).
    /// </summary>
    public decimal ScrapTieToleranceM2 { get; init; }

    /// <summary>
    /// Approved ranking (product owner, 12 Nov 2026): lowest price first (warehouse stock costs nothing, so remnants
    /// come first naturally), then least unusable scrap within <see cref="ScrapTieToleranceM2"/> (0.01 m², a
    /// 100 × 100 mm square), then the biggest keepable leftover for the next jobs, then the least running length of
    /// the new roll, then fewest cuts. Purchased area and warehouse stock committed are final tie-breaks.
    /// </summary>
    public static ObjectivePolicy CheapestFirst { get; } = new()
    {
        Version = "cheapest-first",
        Approved = true,
        ScrapTieToleranceM2 = 0.01m,
        Tiers =
        [
            ObjectiveCriterion.PurchaseCost,
            ObjectiveCriterion.UnusableScrapArea,
            ObjectiveCriterion.LargestReusableRemnantArea,
            ObjectiveCriterion.RollLengthUsed,
            ObjectiveCriterion.TotalCuts,
            ObjectiveCriterion.PurchasedArea,
            ObjectiveCriterion.ExistingStockAreaConsumed,
        ],
    };

    /// <summary>Spec §5 proposed comparator (pending approval). Implementation tie-breaks appended last.</summary>
    public static ObjectivePolicy ProposedV1 { get; } = new()
    {
        Version = "proposed-v1",
        Approved = false,
        Tiers =
        [
            ObjectiveCriterion.PurchasedArea,
            ObjectiveCriterion.UnusableScrapArea,
            ObjectiveCriterion.LargestReusableRemnantArea,
            ObjectiveCriterion.ThinFragmentCount,
            ObjectiveCriterion.PurchaseCost,
            ObjectiveCriterion.TotalCuts,
            ObjectiveCriterion.ExistingStockAreaConsumed,
        ],
    };

    /// <summary>The earlier "low total leftover before cost" business reading, kept for side-by-side review.</summary>
    public static ObjectivePolicy HistoricalWasteFirst { get; } = new()
    {
        Version = "historical-waste-first",
        Approved = false,
        Tiers =
        [
            ObjectiveCriterion.TotalResidualArea,
            ObjectiveCriterion.PurchaseCost,
            ObjectiveCriterion.PurchasedArea,
            ObjectiveCriterion.TotalCuts,
        ],
    };
}

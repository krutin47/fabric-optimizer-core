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

    /// <summary>Stop the improvement phase after this many consecutive iterations without a new best plan.</summary>
    public int StagnationLimit { get; init; } = 1500;

    /// <summary>Maximum number of verified plans retained in the incumbent pool.</summary>
    public int PoolCapacity { get; init; } = 120;

    public OptimizerLimits Limits { get; init; } = new();
}

public sealed record ExactSolverOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Only attempt the exact solver when the job has at most this many piece instances.</summary>
    public int MaxInstances { get; init; } = 12;

    /// <summary>Upper bound on distinct piece multisets (product of (quantity+1) over piece types).</summary>
    public int MaxMultisets { get; init; } = 4096;

    public int MaxRemnants { get; init; } = 6;

    /// <summary>Memo-state cap; exceeding it aborts the exact run without any optimality claim.</summary>
    public int MaxStates { get; init; } = 400_000;

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

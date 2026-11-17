namespace DreamGlass.Optimizer.Contracts;

/// <summary>Run envelope (spec §8).</summary>
public sealed record OptimizationResult
{
    public string RunId { get; init; } = "";

    /// <summary>SHA-256 of the canonical snapshot plus engine options (what would have to be equal to repeat the run).</summary>
    public string InputHash { get; init; } = "";

    /// <summary>SHA-256 of the canonical snapshot alone; every candidate carries the same value.</summary>
    public string SnapshotHash { get; init; } = "";

    public string? OrganizationId { get; init; }
    public string? JobId { get; init; }
    public string JobRevisionId { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Where the run executed (runtime, operating system, processor count). No machine or user names.</summary>
    public RuntimeContext Runtime { get; init; } = new();
    public int SchemaVersion { get; init; }
    public string SolverVersion { get; init; } = "";
    public string ObjectiveVersion { get; init; } = "";
    public bool ObjectiveApproved { get; init; }
    public string ValidatorVersion { get; init; } = "";
    public long Seed { get; init; }
    public ExecutionMode Mode { get; init; }
    public long ElapsedMs { get; init; }
    public long TimeBudgetMs { get; init; }
    public RunStatus Status { get; init; }
    public StopReason StopReason { get; init; }

    /// <summary>SHA-256 over the canonical candidate content (excludes run id and timings) — for determinism checks.</summary>
    public string ResultHash { get; init; } = "";

    public RunDiagnostics Diagnostics { get; init; } = new();
    public IReadOnlyList<InputError> InputErrors { get; init; } = [];
    public IReadOnlyList<CandidateCutPlan> Candidates { get; init; } = [];
}

/// <summary>Non-identifying execution context, recorded so timings and results can be compared (handbook §33).</summary>
public sealed record RuntimeContext
{
    public string Framework { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Architecture { get; init; } = "";
    public int ProcessorCount { get; init; }
}

/// <summary>Structured input error with a field path and remediation (spec §3.7).</summary>
public sealed record InputError(string Code, string Path, string Message, string Remediation);

public sealed record RunDiagnostics
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> InfeasibilityReasons { get; init; } = [];
    public string? FailureMessage { get; init; }
    public int ConstructionsAttempted { get; init; }
    public int CompletePlansBuilt { get; init; }
    public int PlansValidated { get; init; }
    public int PlansRejectedByValidator { get; init; }
    public int BeamRuns { get; init; }
    public int ImprovementIterations { get; init; }
    public int PoolSize { get; init; }
    public long WorkUnitsUsed { get; init; }

    /// <summary>False when a deterministic run hit its wall-clock safety cap (result may not be reproducible).</summary>
    public bool Reproducible { get; init; }

    public IReadOnlyList<PhaseTiming> Phases { get; init; } = [];

    /// <summary>Where the search time went (ms): building layouts, turning them into plans, independent validation.</summary>
    public long ConstructionMs { get; init; }
    public long AssemblyMs { get; init; }
    public long ValidationMs { get; init; }
    public ExactSolveReport? Exact { get; init; }
    public decimal? PurchasedAreaLowerBoundM2 { get; init; }
    public string? PurchasedAreaLowerBoundMethod { get; init; }

    /// <summary>Layouts skipped before validation because the same layout had already been seen.</summary>
    public int DuplicateLayoutsSkipped { get; init; }

    /// <summary>Layouts skipped before validation because they could not improve on the plans already kept.</summary>
    public int UncompetitiveLayoutsSkipped { get; init; }

    public decimal? PurchaseCostLowerBound { get; init; }
    public string? PurchaseCostLowerBoundMethod { get; init; }

    /// <summary>First validator rejection messages (bounded), to surface generator bugs loudly.</summary>
    public IReadOnlyList<string> RejectionSamples { get; init; } = [];

    /// <summary>
    /// Plans the independent validator refused (bounded), each with its structured failures. They are never returned
    /// as candidates; a non-empty list means the generator has a defect worth reporting.
    /// </summary>
    public IReadOnlyList<RejectedCandidate> RejectedCandidates { get; init; } = [];
}

public sealed record RejectedCandidate(string CandidateId, string Origin, IReadOnlyList<ValidationFailure> Failures);

public sealed record PhaseTiming(string Phase, long ElapsedMs, int Iterations);

public sealed record ExactSolveReport
{
    public bool Attempted { get; init; }
    public bool Completed { get; init; }
    public string Reason { get; init; } = "";
    public long StatesExplored { get; init; }

    /// <summary>Splits examined (the exact solver's machine-independent unit of work).</summary>
    public long WorkUnits { get; init; }
    public long ElapsedMs { get; init; }

    /// <summary>Proven minimum newly purchased area over all guillotine plans and source assignments (tier 1 only).</summary>
    public decimal? ProvenMinPurchasedAreaM2 { get; init; }

    /// <summary>Proven minimum purchase cost over all guillotine plans, source assignments and roll offers.</summary>
    public decimal? ProvenMinPurchaseCost { get; init; }
    public bool? ProvenInfeasible { get; init; }
}

/// <summary>One ranked, independently validated plan (spec §8).</summary>
public sealed record CandidateCutPlan
{
    public string CandidateId { get; init; } = "";

    /// <summary>
    /// Hash of the snapshot this plan was computed from (<see cref="OptimizationResult.SnapshotHash"/>). The validator
    /// refuses the plan against any other snapshot, so a plan for an edited job or changed stock cannot be approved.
    /// </summary>
    public string SnapshotHash { get; init; } = "";

    public string JobRevisionId { get; init; } = "";
    public int Rank { get; init; }
    public QualityStatus QualityStatus { get; init; }

    /// <summary>Bound on newly purchased area, when one is known.</summary>
    public ObjectiveBound? Bound { get; init; }

    /// <summary>Bound on purchase cost, when one is known (the first tier of the cheapest-first ranking).</summary>
    public ObjectiveBound? CostBound { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Origin { get; init; } = "";
    public PlanMetrics Metrics { get; init; } = new();
    public bool RequiresPurchase { get; init; }
    public PurchaseLine? Purchase { get; init; }
    public IReadOnlyList<PlanSource> Sources { get; init; } = [];
    public IReadOnlyList<Placement> Placements { get; init; } = [];
    public IReadOnlyList<SourceCutTree> CutTrees { get; init; } = [];
    public IReadOnlyList<CutStep> CutSteps { get; init; } = [];
    public IReadOnlyList<PredictedResidual> PredictedResiduals { get; init; } = [];
    public PlanValidationOutcome Validation { get; init; } = new();
}

/// <summary>Objective bound with an explicit scope — never a claim about the whole lexicographic objective.</summary>
public sealed record ObjectiveBound
{
    /// <summary>"PurchasedAreaM2" or "PurchaseCost".</summary>
    public string Metric { get; init; } = "PurchasedAreaM2";
    public string Scope { get; init; } = "";
    public string Method { get; init; } = "";

    /// <summary>Lower bound in the metric's unit (m² or currency). Kept under the historical name for area bounds.</summary>
    public decimal LowerBoundM2 { get; init; }

    /// <summary>This plan's value minus the bound, in the metric's unit.</summary>
    public decimal GapM2 { get; init; }

    /// <summary>Gap as a percentage of the lower bound (the denominator is the bound, not the plan); null when the bound is 0.</summary>
    public decimal? GapPercentOfBound { get; init; }

    /// <summary>True only when the bound comes from a completed exact search, not from a relaxation.</summary>
    public bool Proven { get; init; }
    public string Note { get; init; } = "";
}

public sealed record RectMm(decimal X, decimal Y, decimal Width, decimal Length);

public sealed record DimensionsMm(decimal Width, decimal Length);

/// <summary>
/// Independent material and work metrics of one plan (handbook §32, §96). They are never folded into one "wastage %".
/// The ledger always balances, and the validator re-derives every line:
/// <list type="bullet">
/// <item>new ordered + existing stock consumed = required + usable leftovers + scrap</item>
/// <item>new ordered = new consumed + rounding surplus</item>
/// <item>existing stock offered = existing stock consumed + existing stock untouched</item>
/// </list>
/// Handbook names: new_ordered_area = <see cref="PurchasedAreaM2"/>, new_consumed_area = <see cref="NewConsumedAreaM2"/>,
/// existing_stock_consumed_area = <see cref="ExistingStockAreaConsumedM2"/>, existing_stock_untouched_area =
/// <see cref="ExistingStockUntouchedAreaM2"/>, usable_predicted_remnant_area = <see cref="ReusableResidualAreaM2"/>,
/// discarded_scrap_area = <see cref="UnusableScrapAreaM2"/>, rounded_surplus_area = <see cref="RoundingSurplusAreaM2"/>,
/// purchase_cost = <see cref="PurchaseCost"/>, cut_count = <see cref="TotalCuts"/>, source_count =
/// <see cref="SourcesUsed"/>, stock_depletion proxy = <see cref="StockDepletionAreaM2"/>.
/// </summary>
public sealed record PlanMetrics
{
    /// <summary>Padded cut-out area of every requested piece.</summary>
    public decimal RequiredCutAreaM2 { get; init; }

    /// <summary>Newly ordered roll area: roll width × ordered whole-metre length.</summary>
    public decimal PurchasedAreaM2 { get; init; }

    /// <summary>Part of the ordered roll the layout uses: roll width × the running length the layout needs.</summary>
    public decimal NewConsumedAreaM2 { get; init; }

    /// <summary>Full-width remainder created by ordering whole metres: roll width × (ordered − needed length).</summary>
    public decimal RoundingSurplusAreaM2 { get; init; }

    public decimal PurchaseCost { get; init; }
    public string? Currency { get; init; }

    /// <summary>Whole area of every warehouse piece the plan cuts into (a touched piece is committed entirely).</summary>
    public decimal ExistingStockAreaConsumedM2 { get; init; }

    /// <summary>Area of offered warehouse pieces the plan leaves untouched. It stays in the warehouse; it is not waste.</summary>
    public decimal ExistingStockUntouchedAreaM2 { get; init; }

    /// <summary>
    /// Warehouse area that does not come back as a keepable leftover: committed warehouse area minus the keepable
    /// leftovers predicted on those pieces. A proxy until the owner decides how to value stock (ADR-015).
    /// </summary>
    public decimal StockDepletionAreaM2 { get; init; }

    /// <summary>All leftover area, keepable or not.</summary>
    public decimal TotalResidualAreaM2 { get; init; }

    /// <summary>Predicted leftovers big enough to keep (not inventory until confirmed after cutting).</summary>
    public decimal ReusableResidualAreaM2 { get; init; }

    /// <summary>Leftovers too small to keep.</summary>
    public decimal UnusableScrapAreaM2 { get; init; }
    public int ReusableOffcutCount { get; init; }
    public IReadOnlyList<DimensionsMm> ReusableOffcuts { get; init; } = [];
    public decimal LargestReusableAreaM2 { get; init; }
    public decimal? MinReusableDimensionMm { get; init; }

    /// <summary>Number of residual leaves.</summary>
    public int FragmentCount { get; init; }

    /// <summary>Residual leaves whose short side is below the remnant policy minimum width.</summary>
    public int ThinFragmentCount { get; init; }

    /// <summary>1 − (largest residual leaf area ÷ total residual area), rounded to 6 dp; 0 = a single leftover piece.</summary>
    public decimal FragmentationIndex { get; init; }

    public decimal? ConsumedLengthMm { get; init; }
    public decimal? OrderedLengthMetres { get; init; }
    public decimal? RoundingSurplusMm { get; init; }
    public int TotalCuts { get; init; }
    public int SourcesUsed { get; init; }
    public int WarehouseSourcesUsed { get; init; }

    /// <summary>Required area ÷ (purchased + consumed stock area), rounded to 6 dp.</summary>
    public decimal MaterialUtilization { get; init; }
}

public sealed record PurchaseLine
{
    public string RollOfferId { get; init; } = "";
    public decimal OrderedWidthMm { get; init; }
    public decimal OrderedLengthMetres { get; init; }
    /// <summary>Roll length the layout needs, including padding (= <see cref="LengthWithoutPaddingMm"/> + <see cref="PaddingLengthMm"/>).</summary>
    public decimal ConsumedLengthMm { get; init; }

    /// <summary>
    /// Part of <see cref="ConsumedLengthMm"/> caused by padding for this exact arrangement: the cut tree's compacted
    /// length with padded footprints minus the same tree's compacted length with finished (nominal) panels.
    /// </summary>
    public decimal PaddingLengthMm { get; init; }

    /// <summary><see cref="ConsumedLengthMm"/> − <see cref="PaddingLengthMm"/>.</summary>
    public decimal LengthWithoutPaddingMm { get; init; }

    public decimal RoundingSurplusMm { get; init; }
    public decimal PurchasedAreaM2 { get; init; }
    public decimal PricePerSquareMetre { get; init; }
    /// <summary>Exact price: purchased area × price per m², not rounded (used for ranking).</summary>
    public decimal PurchaseCost { get; init; }

    /// <summary><see cref="PurchaseCost"/> rounded for documents by <see cref="CostRounding"/>.</summary>
    public decimal PurchaseCostRounded { get; init; }

    /// <summary>The rounding rule applied to <see cref="PurchaseCostRounded"/>.</summary>
    public string CostRounding { get; init; } = Money.RoundingRule;

    public string? Currency { get; init; }
    public bool FixedLength { get; init; }
}

/// <summary>Money presentation rule shared by the generator and the validator contract (handbook §15).</summary>
public static class Money
{
    public const string RoundingRule = "half-up to 2 decimals";

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

public sealed record PlanSource
{
    public string SourceId { get; init; } = "";
    public SourceKind Kind { get; init; }
    public string? StockId { get; init; }
    public long? StockVersion { get; init; }
    public string? RollOfferId { get; init; }
    public string? BinReference { get; init; }

    /// <summary>Canonical (unrotated) dimensions: width along x, length along y.</summary>
    public decimal WidthMm { get; init; }
    public decimal LengthMm { get; init; }
    public PatternAxis PatternAxis { get; init; }

    /// <summary>
    /// Rigid display rotation for the cutting table / printout: 0, 90 (clockwise) or 270 (counter-clockwise). v1.2 draws
    /// every source at 270: length (running length) left to right from the start of the roll, width top to bottom.
    /// </summary>
    public int DisplayRotationDegrees { get; init; }
    public decimal DisplayWidthMm { get; init; }
    public decimal DisplayLengthMm { get; init; }
    public decimal AreaM2 { get; init; }
}

public sealed record Placement
{
    /// <summary>Stable instance id: <c>{pieceId}#{ordinal}</c>, ordinal 1..quantity.</summary>
    public string PieceInstanceId { get; init; } = "";
    public string PieceId { get; init; } = "";
    public string Label { get; init; } = "";
    public string SourceId { get; init; } = "";

    /// <summary>Padded cutting footprint in canonical source coordinates (half-open).</summary>
    public RectMm Footprint { get; init; } = new(0, 0, 0, 0);

    /// <summary>Nominal (finished) panel rectangle inside the footprint, in canonical source coordinates.</summary>
    public RectMm NominalRect { get; init; } = new(0, 0, 0, 0);

    public decimal NominalWidthMm { get; init; }
    public decimal NominalLengthMm { get; init; }

    /// <summary>Full cut-out width including padding, in the finished-panel frame (nominal width + left + right).</summary>
    public decimal CutWidthMm { get; init; }

    /// <summary>Full cut-out length including padding, in the finished-panel frame (nominal length + top + bottom).</summary>
    public decimal CutLengthMm { get; init; }
    public Padding PaddingMm { get; init; } = new();

    /// <summary>True when the panel is rotated 90° clockwise relative to the source's canonical frame.</summary>
    public bool PieceRotated { get; init; }
    public int PieceRotationDegrees { get; init; }
    public int SourceDisplayRotationDegrees { get; init; }

    /// <summary>Resulting pattern direction on the finished panel (None for non-directional material).</summary>
    public PatternOrientation FinishedPatternDirection { get; init; }

    /// <summary>The pattern direction the job asked for on this piece.</summary>
    public PatternOrientation RequiredOrientation { get; init; }

    /// <summary>Pattern axis of the source this piece is cut from, in the source's own (canonical) frame.</summary>
    public PatternAxis SourcePatternAxis { get; init; }
}

/// <summary>Flat, id-linked cut tree (robust to deep trees; lets the validator detect cycles and orphans).</summary>
public sealed record SourceCutTree
{
    public string SourceId { get; init; } = "";
    public string RootNodeId { get; init; } = "";
    public IReadOnlyList<CutTreeNode> Nodes { get; init; } = [];
}

public sealed record CutTreeNode
{
    public string NodeId { get; init; } = "";
    public CutNodeKind Kind { get; init; }
    public RectMm Region { get; init; } = new(0, 0, 0, 0);
    public CutAxis? Axis { get; init; }
    public decimal? PositionMm { get; init; }

    /// <summary>Left child (vertical cut) or top child (horizontal cut).</summary>
    public string? FirstChildId { get; init; }

    /// <summary>Right child (vertical cut) or bottom child (horizontal cut).</summary>
    public string? SecondChildId { get; init; }

    public string? PieceInstanceId { get; init; }
    public string? ResidualId { get; init; }
}

/// <summary>One edge-to-edge cut in deterministic execution order (parents before children).</summary>
public sealed record CutStep
{
    public int Sequence { get; init; }
    public string SourceId { get; init; } = "";
    public string NodeId { get; init; } = "";
    public CutAxis Axis { get; init; }
    public decimal PositionMm { get; init; }
    public RectMm Region { get; init; } = new(0, 0, 0, 0);
    public decimal FromXMm { get; init; }
    public decimal FromYMm { get; init; }
    public decimal ToXMm { get; init; }
    public decimal ToYMm { get; init; }
}

public sealed record PredictedResidual
{
    public string ResidualId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public RectMm Region { get; init; } = new(0, 0, 0, 0);
    public decimal AreaM2 { get; init; }
    public bool Eligible { get; init; }
    public string Reason { get; init; } = "";
    public bool RoundingSurplus { get; init; }
}

public sealed record PlanValidationOutcome
{
    public string ValidatorVersion { get; init; } = "";
    public bool Passed { get; init; }
    public IReadOnlyList<ValidationFailure> Failures { get; init; } = [];
}

public sealed record ValidationFailure(
    string Code,
    string Message,
    string? SourceId = null,
    string? PieceInstanceId = null,
    string? NodeId = null);

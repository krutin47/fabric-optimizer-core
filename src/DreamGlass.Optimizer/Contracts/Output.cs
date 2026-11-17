namespace DreamGlass.Optimizer.Contracts;

/// <summary>Run envelope (spec §8).</summary>
public sealed record OptimizationResult
{
    public string RunId { get; init; } = "";
    public string InputHash { get; init; } = "";
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
    public ExactSolveReport? Exact { get; init; }
    public decimal? PurchasedAreaLowerBoundM2 { get; init; }
    public string? PurchasedAreaLowerBoundMethod { get; init; }

    /// <summary>First validator rejection messages (bounded), to surface generator bugs loudly.</summary>
    public IReadOnlyList<string> RejectionSamples { get; init; } = [];
}

public sealed record PhaseTiming(string Phase, long ElapsedMs, int Iterations);

public sealed record ExactSolveReport
{
    public bool Attempted { get; init; }
    public bool Completed { get; init; }
    public string Reason { get; init; } = "";
    public long StatesExplored { get; init; }
    public long ElapsedMs { get; init; }

    /// <summary>Proven minimum newly purchased area over all guillotine plans and source assignments (tier 1 only).</summary>
    public decimal? ProvenMinPurchasedAreaM2 { get; init; }
    public bool? ProvenInfeasible { get; init; }
}

/// <summary>One ranked, independently validated plan (spec §8).</summary>
public sealed record CandidateCutPlan
{
    public string CandidateId { get; init; } = "";
    public int Rank { get; init; }
    public QualityStatus QualityStatus { get; init; }
    public ObjectiveBound? Bound { get; init; }
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
    public string Scope { get; init; } = "";
    public string Method { get; init; } = "";
    public decimal LowerBoundM2 { get; init; }
    public decimal GapM2 { get; init; }
    public string Note { get; init; } = "";
}

public sealed record RectMm(decimal X, decimal Y, decimal Width, decimal Length);

public sealed record DimensionsMm(decimal Width, decimal Length);

public sealed record PlanMetrics
{
    public decimal RequiredCutAreaM2 { get; init; }
    public decimal PurchasedAreaM2 { get; init; }
    public decimal PurchaseCost { get; init; }
    public string? Currency { get; init; }
    public decimal ExistingStockAreaConsumedM2 { get; init; }
    public decimal TotalResidualAreaM2 { get; init; }
    public decimal ReusableResidualAreaM2 { get; init; }
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
    public decimal PurchaseCost { get; init; }
    public string? Currency { get; init; }
    public bool FixedLength { get; init; }
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

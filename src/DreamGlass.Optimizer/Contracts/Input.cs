namespace DreamGlass.Optimizer.Contracts;

/// <summary>
/// Immutable business input for one optimisation run (spec §3). All lengths are millimetres expressed as
/// <see cref="decimal"/> so that no input is silently rounded; they are converted to scaled integers using
/// <see cref="Precision"/> before search.
/// </summary>
public sealed record OptimizationSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string JobRevisionId { get; init; } = "";
    public string MaterialId { get; init; } = "";
    public bool MaterialDirectional { get; init; }
    public IReadOnlyList<PieceRequest> Pieces { get; init; } = [];
    public IReadOnlyList<RollOffer> RollOffers { get; init; } = [];
    public IReadOnlyList<StockItem> Stock { get; init; } = [];

    /// <summary>
    /// Illustrative remnant-eligibility policy. <b>Open business decision (spec §12.2)</b>; when absent every
    /// residual is reported as not eligible and a warning is raised.
    /// </summary>
    public RemnantPolicy? MinimumReusableRemnant { get; init; }

    public PrecisionPolicy Precision { get; init; } = new();
    public SearchSettings Search { get; init; } = new();
}

/// <summary>A requested rectangular panel. Width/length are in the finished-panel frame.</summary>
public sealed record PieceRequest
{
    public string Id { get; init; } = "";
    public string? Label { get; init; }
    public decimal WidthMm { get; init; }
    public decimal LengthMm { get; init; }
    public int Quantity { get; init; } = 1;
    public PatternOrientation Orientation { get; init; } = PatternOrientation.None;

    /// <summary>Per-side padding in the finished-panel frame (top/bottom along length, left/right along width).</summary>
    public Padding PaddingMm { get; init; } = new();
}

public sealed record Padding
{
    public decimal Top { get; init; }
    public decimal Right { get; init; }
    public decimal Bottom { get; init; }
    public decimal Left { get; init; }
}

/// <summary>A purchasable roll width. Production mode orders an integer number of metres.</summary>
public sealed record RollOffer
{
    public string OfferId { get; init; } = "";
    public decimal WidthMm { get; init; }
    public decimal PricePerSquareMetre { get; init; }
    public string? Currency { get; init; }

    /// <summary>Smallest orderable length in metres.</summary>
    public int MinOrderMetres { get; init; } = 1;

    /// <summary>Largest continuous orderable length in metres (supplier limit), if any.</summary>
    public int? MaxOrderMetres { get; init; }

    /// <summary>Permitted ordered lengths are <c>MinOrderMetres + k * OrderIncrementMetres</c>.</summary>
    public int OrderIncrementMetres { get; init; } = 1;

    public PatternAxis PatternAxis { get; init; } = PatternAxis.Length;

    /// <summary>
    /// Fixed-stock test mode (spec §1): the new source has exactly this length (e.g. 25,000 mm) and no
    /// whole-metre rounding is applied.
    /// </summary>
    public decimal? FixedLengthMm { get; init; }
}

/// <summary>A confirmed, available physical warehouse remnant.</summary>
public sealed record StockItem
{
    public string StockId { get; init; } = "";
    public decimal WidthMm { get; init; }
    public decimal LengthMm { get; init; }
    public PatternAxis PatternAxis { get; init; } = PatternAxis.Length;
    public long Version { get; init; }
    public string? MaterialId { get; init; }
    public bool Available { get; init; } = true;
    public string? BinReference { get; init; }
}

/// <summary>
/// Illustrative eligibility rule for retaining a residual as warehouse stock. Dimensions are orientation
/// independent: <see cref="MinWidthMm"/> applies to the short side and <see cref="MinLengthMm"/> to the long side.
/// </summary>
public sealed record RemnantPolicy
{
    public decimal MinWidthMm { get; init; }
    public decimal MinLengthMm { get; init; }
    public decimal? MinAreaM2 { get; init; }
    public decimal? MaxAspectRatio { get; init; }
}

/// <summary>Scaled-integer precision used by solver geometry. Input with finer precision is rejected, never rounded.</summary>
public sealed record PrecisionPolicy
{
    /// <summary>Integer units per millimetre: 1, 10, 100 or 1000.</summary>
    public int UnitsPerMm { get; init; } = 10;
}

public sealed record SearchSettings
{
    public int TimeBudgetMs { get; init; } = 60_000;
    public long Seed { get; init; } = 1;
    public int MaxCandidates { get; init; } = 3;
}

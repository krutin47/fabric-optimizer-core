using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Normalization;

/// <summary>A legal placement orientation: padded footprint dimensions in the source's canonical frame.</summary>
internal readonly record struct Orientation(bool Rotated, long W, long H);

internal sealed class PieceType
{
    public required int Index { get; init; }
    public required PieceRequest Request { get; init; }
    public required string PieceId { get; init; }
    public required string Label { get; init; }
    public required long NomW { get; init; }
    public required long NomL { get; init; }
    public required long PadT { get; init; }
    public required long PadR { get; init; }
    public required long PadB { get; init; }
    public required long PadL { get; init; }
    public required PatternOrientation Required { get; init; }
    public required int Quantity { get; init; }

    /// <summary>Legal orientations on a source whose pattern runs along its length.</summary>
    public required Orientation[] OnLengthAxis { get; init; }

    /// <summary>Legal orientations on a source whose pattern runs along its width.</summary>
    public required Orientation[] OnWidthAxis { get; init; }

    public long FootW => NomW + PadL + PadR;
    public long FootL => NomL + PadT + PadB;
    public long FootArea => FootW * FootL;
    public long MaxSide => Math.Max(FootW, FootL);
    public long MinSide => Math.Min(FootW, FootL);

    public Orientation[] For(PatternAxis axis) => axis == PatternAxis.Length ? OnLengthAxis : OnWidthAxis;
}

internal sealed class Instance
{
    public required int Index { get; init; }
    public required int Type { get; init; }
}

internal sealed class SourceSpec
{
    public required int Index { get; init; }
    public required SourceKind Kind { get; init; }
    public required string SourceId { get; init; }
    public required long W { get; init; }

    /// <summary>Remnant: physical length. Fixed-length roll: that length. Ordered roll: 0 (decided by the plan).</summary>
    public required long L { get; init; }

    public required PatternAxis Axis { get; init; }
    public StockItem? Stock { get; init; }
    public RollOffer? Offer { get; init; }

    public long MetreUnits { get; init; }
    public long? FixedLengthUnits { get; init; }
    public int MinOrderMetres { get; init; } = 1;
    public int? MaxOrderMetres { get; init; }
    public int IncrementMetres { get; init; } = 1;

    public bool IsRoll => Kind == SourceKind.NewRoll;
    public long Area => W * L;

    /// <summary>Plan group this source puts a plan in: "roll:{offerId}" for a roll offer.</summary>
    public string Group { get; init; } = "warehouse-only";

    /// <summary>Longest roll this offer can supply, if limited.</summary>
    public long? MaxLengthUnits => FixedLengthUnits ?? (MaxOrderMetres is int m ? m * MetreUnits : null);
}

internal sealed record RemnantPolicyUnits(long MinShort, long MinLong, decimal? MinAreaM2, decimal? MaxAspectRatio);

internal sealed class NormalizedProblem
{
    public required OptimizationSnapshot Snapshot { get; init; }

    /// <summary>Hash of the canonical snapshot; stamped on every candidate (handbook §97).</summary>
    public required string SnapshotHash { get; init; }

    public required long Scale { get; init; }
    public required bool Directional { get; init; }
    public required PieceType[] Types { get; init; }
    public required Instance[] Instances { get; init; }

    /// <summary>All sources: remnants first (input order), then one entry per roll offer.</summary>
    public required SourceSpec[] Sources { get; init; }

    public required int[] RemnantSources { get; init; }
    public required int[] RollSources { get; init; }
    public RemnantPolicyUnits? Policy { get; init; }
    public required long RequiredArea { get; init; }
    public required long TotalRemnantArea { get; init; }

    /// <summary>Shortest side of any piece footprint: a free region narrower than this can never take a piece.</summary>
    public long MinPieceSide { get; init; }

    /// <summary>Smallest piece footprint area: a free region smaller than this can never take a piece.</summary>
    public long MinPieceArea { get; init; }

    /// <summary>Instance indices, largest footprint first (ties by index). Fixed for the job, so it is sorted once.</summary>
    public required int[] InstancesByAreaDesc { get; init; }

    public PieceType TypeOf(int instance) => Types[Instances[instance].Type];

    /// <summary>
    /// Smallest permitted ordered roll length (units) covering <paramref name="consumed"/>, or null when the
    /// supplier limits cannot cover it. Permitted lengths are <c>min + k·increment</c> metres.
    /// </summary>
    public static long? OrderedLength(SourceSpec roll, long consumed)
    {
        if (roll.FixedLengthUnits is long fixedLen) return consumed <= fixedLen ? fixedLen : null;
        long mu = roll.MetreUnits;
        long metres = (consumed + mu - 1) / mu;
        if (metres < roll.MinOrderMetres) metres = roll.MinOrderMetres;
        long over = metres - roll.MinOrderMetres;
        long k = (over + roll.IncrementMetres - 1) / roll.IncrementMetres;
        metres = roll.MinOrderMetres + k * roll.IncrementMetres;
        if (roll.MaxOrderMetres is int max && metres > max) return null;
        return metres * mu;
    }

    /// <summary>Orientation-agnostic stacking bound used as the trial length of a fresh roll.</summary>
    public long RollTrialLength(SourceSpec roll)
    {
        long sum = 0;
        foreach (var inst in Instances) sum += Types[inst.Type].MaxSide;
        if (roll.FixedLengthUnits is long f) return f;
        if (roll.MaxLengthUnits is long max) return Math.Min(sum, max);
        return sum;
    }
}

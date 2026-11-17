namespace DreamGlass.Optimizer.Solver;

/// <summary>How placement candidates across sources are prioritised (joint remnant / new-roll assignment).</summary>
internal enum TierOrder
{
    /// <summary>Minimise roll length growth first, then prefer already-opened sources, then fit.</summary>
    MinExtension,
    /// <summary>Use any remnant before the roll; the roll only takes what remnants cannot.</summary>
    RemnantsFirst,
    /// <summary>Prefer sources that are already cut, to commit as little warehouse stock as possible.</summary>
    OpenedFirst,
    /// <summary>Pure best-fit across all sources, roll growth as the tie-break.</summary>
    FitFirst,
}

/// <summary>Which untouched remnant to open first.</summary>
internal enum OpenPreference
{
    SmallestFirst,
    LargestFirst,
    NoPreference,
}

internal enum FitRule
{
    BestAreaFit,
    BestShortSideFit,
    BestLongSideFit,
    BottomLeft,
}

/// <summary>Which of the two guillotine split orders isolates a placed piece (spec §6, "both allowed split directions").</summary>
internal enum SplitRule
{
    HorizontalFirst,
    VerticalFirst,
    ShorterLeftoverAxis,
    LongerLeftoverAxis,
    MaximizeLargerResidual,
    MinimizeSmallerResidual,
}

/// <remarks>
/// <c>Rows</c> turns on row sections (rolls only): when a piece opens a new section of the roll, the section may be as
/// long as a row of pending pieces side by side rather than the opening piece alone (see <see cref="Construction.ChooseSection"/>).
/// </remarks>
internal sealed record PlacementPolicy(TierOrder Tiers, OpenPreference Open, FitRule Fit, SplitRule Split, bool RandomTies, bool Rows = false)
{
    public override string ToString() => $"{Tiers}/{Open}/{Fit}/{Split}{(RandomTies ? "/rt" : "")}{(Rows ? "/rows" : "")}";

    public bool HorizontalFirst(Rect leaf, long w, long h)
    {
        long dw = leaf.W - w, dh = leaf.H - h;
        return Split switch
        {
            SplitRule.HorizontalFirst => true,
            SplitRule.VerticalFirst => false,
            SplitRule.ShorterLeftoverAxis => dw <= dh,
            SplitRule.LongerLeftoverAxis => dw > dh,
            // Horizontal-first leaves (dw × h) and (W × dh); vertical-first leaves (dw × H) and (w × dh).
            SplitRule.MaximizeLargerResidual => Math.Max(dw * h, leaf.W * dh) >= Math.Max(dw * leaf.H, w * dh),
            SplitRule.MinimizeSmallerResidual => Math.Min(dw * h, leaf.W * dh) <= Math.Min(dw * leaf.H, w * dh),
            _ => true,
        };
    }
}

/// <summary>Lexicographic placement score (lower is better).</summary>
internal readonly record struct Score(long A, long B, long C, long D, long E, long F) : IComparable<Score>
{
    public static readonly Score Max = new(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue);

    public int CompareTo(Score o)
    {
        int c = A.CompareTo(o.A);
        if (c != 0) return c;
        c = B.CompareTo(o.B);
        if (c != 0) return c;
        c = C.CompareTo(o.C);
        if (c != 0) return c;
        c = D.CompareTo(o.D);
        if (c != 0) return c;
        c = E.CompareTo(o.E);
        return c != 0 ? c : F.CompareTo(o.F);
    }

    public static bool operator <(Score a, Score b) => a.CompareTo(b) < 0;
    public static bool operator >(Score a, Score b) => a.CompareTo(b) > 0;
}

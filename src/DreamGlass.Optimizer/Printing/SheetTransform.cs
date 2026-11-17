using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Printing;

/// <summary>
/// The coordinate transformations of the cut plan, in one place (handbook §30, §95). Every one has an inverse and
/// the test suite proves the round trips.
/// <para>
/// <b>Canonical frame</b> (what the plan stores): origin at one corner of the physical source, <c>x</c> across the
/// source width, <c>y</c> along its length. The pattern axis is attached to the source in this frame.
/// </para>
/// <para>
/// <b>Display frame</b> (what a drawing or a table shows): the canonical frame turned rigidly by the source's
/// <c>displayRotationDegrees</c> — 0, 90 (clockwise) or 270 (counter-clockwise, the default: length left to right,
/// width top to bottom). Turning the whole sheet turns the pattern with it, so it never changes which cuts are legal.
/// </para>
/// </summary>
public static class SheetTransform
{
    private static ArgumentOutOfRangeException Unsupported(int rotation) =>
        new(nameof(rotation), rotation, "Display rotation must be 0, 90 or 270 degrees.");

    /// <summary>Size of the sheet as displayed: (left-to-right, top-to-bottom).</summary>
    public static (decimal Across, decimal Down) DisplaySize(decimal widthMm, decimal lengthMm, int rotation) => rotation switch
    {
        0 => (widthMm, lengthMm),
        90 or 270 => (lengthMm, widthMm),
        _ => throw Unsupported(rotation),
    };

    /// <summary>Canonical point → display point.</summary>
    public static (decimal X, decimal Y) ToDisplay(decimal x, decimal y, decimal widthMm, decimal lengthMm, int rotation) => rotation switch
    {
        0 => (x, y),
        270 => (y, widthMm - x),
        90 => (lengthMm - y, x),
        _ => throw Unsupported(rotation),
    };

    /// <summary>Display point → canonical point (inverse of <see cref="ToDisplay(decimal, decimal, decimal, decimal, int)"/>).</summary>
    public static (decimal X, decimal Y) ToCanonical(decimal displayX, decimal displayY, decimal widthMm, decimal lengthMm, int rotation) => rotation switch
    {
        0 => (displayX, displayY),
        270 => (widthMm - displayY, displayX),
        90 => (displayY, lengthMm - displayX),
        _ => throw Unsupported(rotation),
    };

    /// <summary>
    /// Canonical rectangle → display rectangle. In the result <see cref="RectMm.Width"/> is the left-to-right extent
    /// and <see cref="RectMm.Length"/> the top-to-bottom extent, with (X, Y) the top-left corner as displayed.
    /// </summary>
    public static RectMm ToDisplay(RectMm r, decimal widthMm, decimal lengthMm, int rotation) => rotation switch
    {
        0 => r,
        270 => new RectMm(r.Y, widthMm - r.X - r.Width, r.Length, r.Width),
        90 => new RectMm(lengthMm - r.Y - r.Length, r.X, r.Length, r.Width),
        _ => throw Unsupported(rotation),
    };

    /// <summary>Display rectangle → canonical rectangle (inverse of <see cref="ToDisplay(RectMm, decimal, decimal, int)"/>).</summary>
    public static RectMm ToCanonical(RectMm d, decimal widthMm, decimal lengthMm, int rotation) => rotation switch
    {
        0 => d,
        270 => new RectMm(widthMm - d.Y - d.Length, d.X, d.Length, d.Width),
        90 => new RectMm(d.Y, lengthMm - d.X - d.Width, d.Length, d.Width),
        _ => throw Unsupported(rotation),
    };

    /// <summary>True when the source's pattern runs left to right on the displayed sheet (otherwise top to bottom).</summary>
    public static bool PatternRunsAcrossDisplay(PatternAxis sourceAxis, int rotation) => rotation switch
    {
        0 => sourceAxis == PatternAxis.Width,
        90 or 270 => sourceAxis == PatternAxis.Length,
        _ => throw Unsupported(rotation),
    };

    /// <summary>
    /// A piece's padding as it lies on the source. Unrotated, panel sides and source sides coincide. Rotated (a
    /// quarter turn clockwise in the canonical frame): source-top = panel-left, source-right = panel-top,
    /// source-bottom = panel-right, source-left = panel-bottom.
    /// </summary>
    public static Padding PaddingOnSource(Padding panel, bool pieceRotated) => pieceRotated
        ? new Padding { Top = panel.Left, Right = panel.Top, Bottom = panel.Right, Left = panel.Bottom }
        : panel;

    /// <summary>Inverse of <see cref="PaddingOnSource"/>: padding on the source → padding in the finished-panel frame.</summary>
    public static Padding PaddingOnPanel(Padding onSource, bool pieceRotated) => pieceRotated
        ? new Padding { Top = onSource.Right, Right = onSource.Bottom, Bottom = onSource.Left, Left = onSource.Top }
        : onSource;

    /// <summary>
    /// Footprint a cut-out of <paramref name="cutWidthMm"/> × <paramref name="cutLengthMm"/> (finished-panel frame)
    /// occupies on the source: (across the source width, along the source length).
    /// </summary>
    public static (decimal AcrossWidth, decimal AlongLength) FootprintOnSource(decimal cutWidthMm, decimal cutLengthMm, bool pieceRotated) =>
        pieceRotated ? (cutLengthMm, cutWidthMm) : (cutWidthMm, cutLengthMm);

    /// <summary>
    /// Pattern direction on the finished panel when it is cut from a source with <paramref name="sourceAxis"/> at the
    /// given rotation. It depends only on the piece's rotation relative to the material, never on how the sheet is drawn.
    /// </summary>
    public static PatternOrientation FinishedPattern(PatternAxis sourceAxis, bool pieceRotated) =>
        (sourceAxis == PatternAxis.Length) != pieceRotated ? PatternOrientation.Vertical : PatternOrientation.Horizontal;
}

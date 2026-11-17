using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Normalization;

/// <summary>
/// Solver-side grain/orientation semantics (spec §4). Conventions — shared contract with the validator, which
/// re-implements them independently:
/// <list type="bullet">
/// <item>Piece rotation is 90° <b>clockwise</b> in the source's canonical frame (x right, y down).</item>
/// <item>Unrotated: panel width → source x, panel length → source y. Rotated: panel length → source x.</item>
/// <item>Rotated padding mapping: source-top = panel-left, source-right = panel-top,
///       source-bottom = panel-right, source-left = panel-bottom.</item>
/// <item>Legality depends only on the piece's rotation <i>relative to the source material</i>. A rigid 90°
///       rotation of a whole source is a display transform: it rotates the grain with the sheet, so it never
///       changes which cuts are legal. Of the four (source × piece) rotation combinations, only the two
///       relative rotations are searched; the source rotation is chosen for presentation and persisted.</item>
/// </list>
/// </summary>
internal static class OrientationRules
{
    /// <summary>Pattern direction on the finished panel for a piece cut at the given rotation from a source.</summary>
    public static PatternOrientation FinishedDirection(PatternAxis sourceAxis, bool pieceRotated) =>
        (sourceAxis, pieceRotated) switch
        {
            (PatternAxis.Length, false) => PatternOrientation.Vertical,
            (PatternAxis.Length, true) => PatternOrientation.Horizontal,
            (PatternAxis.Width, false) => PatternOrientation.Horizontal,
            (PatternAxis.Width, true) => PatternOrientation.Vertical,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceAxis)),
        };

    public static bool IsAllowed(bool materialDirectional, PatternOrientation required, PatternAxis sourceAxis, bool pieceRotated) =>
        !materialDirectional
        || required == PatternOrientation.None
        || FinishedDirection(sourceAxis, pieceRotated) == required;

    /// <summary>
    /// Display policy for printouts: every source is drawn as unrolled on the cutting table, its length (running
    /// length) left to right from the start of the roll and its width (the roll's fixed height) top to bottom. That is
    /// a quarter turn counter-clockwise of the canonical frame: (x, y) ↦ (y, W − x).
    /// </summary>
    public const int SheetDisplayRotationDegrees = 270;
}

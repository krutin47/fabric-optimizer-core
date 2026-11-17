using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Spec §4 grain semantics; §10 "Grain lock" and "Remnant rotation".</summary>
public class OrientationTests
{
    [Theory]
    [InlineData(PatternAxis.Length, false, PatternOrientation.Vertical)]
    [InlineData(PatternAxis.Length, true, PatternOrientation.Horizontal)]
    [InlineData(PatternAxis.Width, false, PatternOrientation.Horizontal)]
    [InlineData(PatternAxis.Width, true, PatternOrientation.Vertical)]
    public void Finished_direction_follows_relative_rotation(PatternAxis axis, bool rotated, PatternOrientation expected) =>
        Assert.Equal(expected, OrientationRules.FinishedDirection(axis, rotated));

    [Fact]
    public void Legal_orientation_sets_respect_grain_lock_none_and_nondirectional_material()
    {
        Orientation[] Legal(bool directional, PatternOrientation req, PatternAxis axis) =>
            Normalizer.LegalOrientations(directional, req, axis, footW: 400, footL: 600);

        Assert.Equal([false], Legal(true, PatternOrientation.Vertical, PatternAxis.Length).Select(o => o.Rotated));
        Assert.Equal([true], Legal(true, PatternOrientation.Horizontal, PatternAxis.Length).Select(o => o.Rotated));
        Assert.Equal([true], Legal(true, PatternOrientation.Vertical, PatternAxis.Width).Select(o => o.Rotated));
        Assert.Equal(2, Legal(true, PatternOrientation.None, PatternAxis.Length).Length);
        Assert.Equal(2, Legal(false, PatternOrientation.Vertical, PatternAxis.Length).Length);

        var rotated = Legal(true, PatternOrientation.Horizontal, PatternAxis.Length).Single();
        Assert.Equal((600L, 400L), (rotated.W, rotated.H));
    }

    [Fact]
    public void Square_footprints_with_both_rotations_legal_are_reduced_to_one_state()
    {
        Assert.Single(Normalizer.LegalOrientations(true, PatternOrientation.None, PatternAxis.Length, 500, 500));
        Assert.Single(Normalizer.LegalOrientations(true, PatternOrientation.Horizontal, PatternAxis.Length, 500, 500));
    }

    [Fact]
    public void Vertical_piece_is_not_rotated_on_a_roll_and_horizontal_piece_is()
    {
        var job = T.Job(
            [T.Piece("V", 400, 600, o: PatternOrientation.Vertical), T.Piece("H", 2000, 300, o: PatternOrientation.Horizontal)],
            [T.Roll("roll", 2800)]);
        var best = T.Best(T.RunOk(job));
        var v = best.Placements.Single(p => p.PieceId == "V");
        var h = best.Placements.Single(p => p.PieceId == "H");
        Assert.False(v.PieceRotated);
        Assert.Equal(PatternOrientation.Vertical, v.FinishedPatternDirection);
        Assert.True(h.PieceRotated);
        Assert.Equal(PatternOrientation.Horizontal, h.FinishedPatternDirection);
        Assert.Equal((300m, 2000m), (h.Footprint.Width, h.Footprint.Length));
    }

    [Fact]
    public void Grain_lock_blocks_rotation_on_directional_material_but_not_on_nondirectional_material()
    {
        var piece = T.Piece("P", 3000, 500, o: PatternOrientation.Vertical);
        Assert.Equal(RunStatus.InvalidInput, T.Run(T.Job([piece], [T.Roll("r", 2800)])).Status);
        var free = T.RunOk(T.Job([piece], [T.Roll("r", 2800)], directional: false));
        var p = T.Best(free).Placements.Single();
        Assert.True(p.PieceRotated);
        Assert.Equal(PatternOrientation.None, p.FinishedPatternDirection);
    }

    [Fact]
    public void Rotated_directional_remnant_yields_the_required_finished_grain()
    {
        // Remnant whose pattern runs across its canonical width; a Vertical panel must be turned 90° relative to it.
        var job = T.Job(
            [T.Piece("P1", 600, 900, o: PatternOrientation.Vertical, padding: new Padding { Top = 10, Right = 0, Bottom = 30, Left = 20 })],
            stock: [T.Stock("R1", 1400, 800, PatternAxis.Width)]);
        var best = T.Best(T.RunOk(job));
        var src = best.Sources.Single();
        var p = best.Placements.Single();
        Assert.True(p.PieceRotated);
        Assert.Equal(PatternOrientation.Vertical, p.FinishedPatternDirection);
        // Sheets are drawn as unrolled on the table: length (800) left to right, width (1400) top to bottom.
        Assert.Equal(270, src.DisplayRotationDegrees);
        Assert.Equal((800m, 1400m), (src.DisplayWidthMm, src.DisplayLengthMm));
        Assert.Equal(270, p.SourceDisplayRotationDegrees);

        // Footprint (rotated): x-extent = length + top + bottom, y-extent = width + left + right.
        Assert.Equal((940m, 620m), (p.Footprint.Width, p.Footprint.Length));
        // Rotated padding: source-left = panel-bottom (30), source-top = panel-left (20).
        Assert.Equal(p.Footprint.X + 30, p.NominalRect.X);
        Assert.Equal(p.Footprint.Y + 20, p.NominalRect.Y);
        Assert.Equal((900m, 600m), (p.NominalRect.Width, p.NominalRect.Length));
        Assert.False(best.RequiresPurchase);
    }

    [Fact]
    public void Horizontal_piece_that_only_fits_unrotated_against_a_width_grain_remnant_is_rejected_up_front()
    {
        var job = T.Job([T.Piece("P1", 600, 900, o: PatternOrientation.Horizontal)], stock: [T.Stock("R1", 1400, 800, PatternAxis.Width)]);
        var r = T.Run(job);
        Assert.Equal(RunStatus.InvalidInput, r.Status);
        Assert.Contains(r.InputErrors, e => e.Code == "PIECE_FITS_NO_SOURCE");
    }
}

using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Spec §3: structured validation before enqueueing; §10 "Width/padding" and "Numeric limits".</summary>
public class InputValidationTests
{
    [Fact]
    public void Padding_that_pushes_a_locked_piece_past_the_roll_width_is_a_structured_error()
    {
        var job = T.Job([T.Piece("P1", 2800, 1000, o: PatternOrientation.Vertical, pad: 5)], [T.Roll("roll-2800", 2800)]);
        var r = T.Run(job);
        Assert.Equal(RunStatus.InvalidInput, r.Status);
        var e = Assert.Single(r.InputErrors);
        Assert.Equal("PIECE_FITS_NO_SOURCE", e.Code);
        Assert.Equal("$.pieces[0]", e.Path);
        Assert.Contains("2810", e.Message);
        Assert.Contains("cannot be turned 90° because its grain must run vertical", e.Message);
        Assert.Contains("as drawn it needs 2810 mm of width", e.Message);
        Assert.False(string.IsNullOrWhiteSpace(e.Remediation));
        Assert.Empty(r.Candidates);
    }

    [Fact]
    public void The_same_piece_fits_without_padding_or_when_rotation_is_allowed()
    {
        T.RunOk(T.Job([T.Piece("P1", 2800, 1000, o: PatternOrientation.Vertical)], [T.Roll("roll-2800", 2800)]));
        var rotated = T.RunOk(T.Job([T.Piece("P1", 2800, 1000, o: PatternOrientation.None, pad: 5)], [T.Roll("roll-2800", 2800)]));
        Assert.True(T.Best(rotated).Placements.Single().PieceRotated);
    }

    [Theory]
    [InlineData(0, 100, "NON_POSITIVE_DIMENSION")]
    [InlineData(-5, 100, "NON_POSITIVE_DIMENSION")]
    [InlineData(100.05, 100, "PRECISION_EXCEEDED")]
    [InlineData(1_000_000_000_000, 100, "DIMENSION_LIMIT")]
    [InlineData(20_000_000, 100, "DIMENSION_LIMIT")]
    public void Bad_or_out_of_range_dimensions_are_rejected_without_overflow(decimal width, decimal length, string code)
    {
        var r = T.Run(T.Job([T.Piece("P1", width, length)], [T.Roll("r", 2800)]));
        Assert.Equal(RunStatus.InvalidInput, r.Status);
        Assert.Contains(r.InputErrors, e => e.Code == code && e.Path.StartsWith("$.pieces[0]"));
    }

    [Fact]
    public void Values_that_would_overflow_scaling_are_rejected_not_thrown()
    {
        var r = T.Run(T.Job([T.Piece("P1", decimal.MaxValue, 100)], [T.Roll("r", 2800)]));
        Assert.Equal(RunStatus.InvalidInput, r.Status);
        Assert.Contains(r.InputErrors, e => e.Code == "PRECISION_EXCEEDED" && e.Path == "$.pieces[0].widthMm");
    }

    [Fact]
    public void Precision_is_never_silently_rounded_but_finer_precision_can_be_enabled()
    {
        var job = T.Job([T.Piece("P1", 100.05m, 100)], [T.Roll("r", 2800)]);
        Assert.Equal(RunStatus.InvalidInput, T.Run(job).Status);
        var fine = job with { Precision = new PrecisionPolicy { UnitsPerMm = 100 } };
        var r = T.RunOk(fine);
        Assert.Equal(100.05m, T.Best(r).Placements.Single().NominalWidthMm);
    }

    [Fact]
    public void Quantity_and_instance_limits_are_enforced()
    {
        var r = T.Run(T.Job([T.Piece("P1", 100, 100, q: 1_000_000)], [T.Roll("r", 2800)]));
        Assert.Contains(r.InputErrors, e => e.Code == "QUANTITY_OUT_OF_RANGE");
        var many = Enumerable.Range(0, 3).Select(i => T.Piece($"P{i}", 100, 100, q: 1000));
        Assert.Contains(T.Run(T.Job(many, [T.Roll("r", 2800)])).InputErrors, e => e.Code == "INSTANCE_LIMIT");
    }

    [Fact]
    public void Aggregate_area_limits_prevent_integer_overflow()
    {
        var options = T.Det() with { Limits = new OptimizerLimits { MaxAggregateAreaUnits = 1_000_000_000_000 } };
        var r = T.Run(T.Job([T.Piece("P1", 9000, 9000, q: 200)], [T.Roll("r", 9000)]), options);
        Assert.Equal(RunStatus.InvalidInput, r.Status);
        Assert.Contains(r.InputErrors, e => e.Code == "AREA_LIMIT");
    }

    [Fact]
    public void Duplicate_ids_missing_material_and_negative_padding_are_reported_with_paths()
    {
        var job = T.Job(
            [T.Piece("A", 100, 100), T.Piece("A", 100, 100, padding: new Padding { Left = -1 })],
            [T.Roll("r", 2800)]) with { MaterialId = "" };
        var codes = InputValidator.Validate(job).Errors.ToDictionary(e => e.Path, e => e.Code);
        Assert.Equal("MATERIAL_MISSING", codes["$.materialId"]);
        Assert.Equal("PIECE_ID_DUPLICATE", codes["$.pieces[1].id"]);
        Assert.Equal("NEGATIVE_VALUE", codes["$.pieces[1].paddingMm.left"]);
    }

    [Fact]
    public void Stock_must_be_available_unique_and_of_the_job_material()
    {
        var job = T.Job(
            [T.Piece("A", 100, 100)],
            stock:
            [
                T.Stock("R1", 500, 500) with { Available = false },
                T.Stock("R2", 500, 500) with { MaterialId = "other" },
                T.Stock("R3", 500, 500),
                T.Stock("R3", 600, 600),
            ]);
        var codes = InputValidator.Validate(job).Errors.Select(e => e.Code).ToList();
        Assert.Contains("STOCK_UNAVAILABLE", codes);
        Assert.Contains("STOCK_MATERIAL_MISMATCH", codes);
        Assert.Contains("STOCK_ID_DUPLICATE", codes);
    }

    [Fact]
    public void A_job_needs_pieces_and_some_source()
    {
        var codes = InputValidator.Validate(T.Job([])).Errors.Select(e => e.Code).ToList();
        Assert.Contains("PIECES_EMPTY", codes);
        Assert.Contains("NO_SOURCES", codes);
        Assert.Equal("INPUT_MISSING", InputValidator.Validate(null).Errors.Single().Code);
    }

    [Fact]
    public void Supplier_limits_are_validated()
    {
        var job = T.Job([T.Piece("A", 100, 100)], [T.Roll("r", 2800, maxMetres: 1, minMetres: 3), T.Roll("r", 1400, increment: 0)]);
        var codes = InputValidator.Validate(job).Errors.Select(e => e.Code).ToList();
        Assert.Contains("MAX_ORDER_INVALID", codes);
        Assert.Contains("OFFER_ID_INVALID", codes);
        Assert.Contains("ORDER_INCREMENT_INVALID", codes);
    }

    [Fact]
    public void Missing_remnant_policy_and_nondirectional_orientation_produce_warnings_not_errors()
    {
        var job = T.Job([T.Piece("A", 100, 100, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)], directional: false, noPolicy: true);
        var outcome = InputValidator.Validate(job);
        Assert.True(outcome.IsValid);
        Assert.Contains(outcome.Warnings, w => w.Contains("non-directional"));
        Assert.Contains(outcome.Warnings, w => w.Contains("remnant policy"));
    }
}

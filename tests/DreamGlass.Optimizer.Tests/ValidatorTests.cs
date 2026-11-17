using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Spec §7 independent validator; §10 "Guillotine trap", "Cut replay", "Source uniqueness".</summary>
public class ValidatorTests
{
    private static readonly CutPlanValidator Validator = new();

    private static (OptimizationSnapshot Job, CandidateCutPlan Plan) MixedPlan()
    {
        var job = T.Job(
            [T.Piece("A", 400, 600, q: 2, o: PatternOrientation.Vertical), T.Piece("B", 300, 300, pad: 5), T.Piece("C", 2000, 700, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800, price: 12.5m)],
            [T.Stock("R1", 1000, 1000), T.Stock("R2", 500, 400)]);
        var r = T.RunOk(job);
        var plan = r.Candidates.First(c => c.Sources.Count >= 2 && c.RequiresPurchase);
        Assert.True(Validator.Validate(job, plan).Passed);
        return (job, plan);
    }

    private static PlanValidationOutcome Check(OptimizationSnapshot job, CandidateCutPlan plan) => Validator.Validate(job, plan);

    [Fact]
    public void Tampered_cut_coordinate_fails_cut_replay()
    {
        var (job, plan) = MixedPlan();
        var tree = plan.CutTrees[0];
        var cut = tree.Nodes.First(n => n.Kind == CutNodeKind.Cut);
        var tampered = tree with { Nodes = tree.Nodes.Select(n => n == cut ? n with { PositionMm = n.PositionMm + 1 } : n).ToList() };
        var outcome = Check(job, plan with { CutTrees = plan.CutTrees.Select(t => t == tree ? tampered : t).ToList() });
        Assert.False(outcome.Passed);
        Assert.Contains("CHILD_REGION_MISMATCH", T.Codes(outcome));
    }

    [Fact]
    public void Missing_residual_leaf_fails()
    {
        var (job, plan) = MixedPlan();
        var outcome = Check(job, plan with { PredictedResiduals = plan.PredictedResiduals.Skip(1).ToList() });
        Assert.False(outcome.Passed);
        Assert.Contains("LEAF_MISMATCH", T.Codes(outcome));
    }

    [Fact]
    public void Forged_cached_metrics_are_rejected()
    {
        var (job, plan) = MixedPlan();
        var outcome = Check(job, plan with { Metrics = plan.Metrics with { UnusableScrapAreaM2 = 0, TotalCuts = 1 } });
        Assert.Contains("METRIC_MISMATCH", T.Codes(outcome));
    }

    [Fact]
    public void Overlap_and_out_of_bounds_are_detected()
    {
        var (job, plan) = MixedPlan();
        var p0 = plan.Placements[0];
        var p1 = plan.Placements.First(p => p.SourceId == p0.SourceId && p != p0 && p.PieceId == p0.PieceId);
        var overlapping = plan with { Placements = plan.Placements.Select(p => p == p1 ? p with { Footprint = p0.Footprint, NominalRect = p0.NominalRect } : p).ToList() };
        Assert.Contains("OVERLAP", T.Codes(Check(job, overlapping)));

        var outside = plan with
        {
            Placements = plan.Placements.Select(p => p == p0 ? p with { Footprint = p.Footprint with { X = -10 }, NominalRect = p.NominalRect with { X = p.NominalRect.X - 10 - p.Footprint.X } } : p).ToList(),
        };
        Assert.Contains("OUT_OF_BOUNDS", T.Codes(Check(job, outside)));
    }

    [Fact]
    public void Rotating_a_grain_locked_piece_is_an_orientation_violation()
    {
        var (job, plan) = MixedPlan();
        var a = plan.Placements.First(p => p.PieceId == "A");
        var turned = a with
        {
            PieceRotated = !a.PieceRotated,
            PieceRotationDegrees = a.PieceRotated ? 0 : 90,
            Footprint = a.Footprint with { Width = a.Footprint.Length, Length = a.Footprint.Width },
        };
        var outcome = Check(job, plan with { Placements = plan.Placements.Select(p => p == a ? turned : p).ToList() });
        Assert.Contains("ORIENTATION_VIOLATION", T.Codes(outcome));
    }

    [Fact]
    public void Missing_duplicate_and_unknown_instances_fail_exact_coverage()
    {
        var (job, plan) = MixedPlan();
        Assert.Contains("MISSING_INSTANCE", T.Codes(Check(job, plan with { Placements = plan.Placements.Skip(1).ToList() })));
        Assert.Contains("DUPLICATE_INSTANCE", T.Codes(Check(job, plan with { Placements = [.. plan.Placements, plan.Placements[0]] })));
        var renamed = plan.Placements.Select((p, i) => i == 0 ? p with { PieceInstanceId = p.PieceId + "#99" } : p).ToList();
        Assert.Contains("UNKNOWN_INSTANCE", T.Codes(Check(job, plan with { Placements = renamed })));
    }

    [Fact]
    public void A_remnant_cannot_appear_twice_and_stale_versions_are_rejected()
    {
        var (job, plan) = MixedPlan();
        var remnant = plan.Sources.First(s => s.Kind == SourceKind.WarehouseRemnant);
        Assert.Contains("DUPLICATE_SOURCE", T.Codes(Check(job, plan with { Sources = [.. plan.Sources, remnant] })));

        var stale = job with { Stock = job.Stock.Select(s => s.StockId == remnant.StockId ? s with { Version = s.Version + 1 } : s).ToList() };
        Assert.Contains("STOCK_MISMATCH", T.Codes(Check(stale, plan)));

        var reserved = job with { Stock = job.Stock.Select(s => s.StockId == remnant.StockId ? s with { Available = false } : s).ToList() };
        Assert.Contains("STOCK_UNAVAILABLE", T.Codes(Check(reserved, plan)));
    }

    [Fact]
    public void Untouched_stock_listed_as_a_source_is_rejected()
    {
        var (job, plan) = MixedPlan();
        var unused = job.Stock.FirstOrDefault(s => plan.Sources.All(p => p.StockId != s.StockId));
        if (unused is null) return; // both remnants used in this plan; covered elsewhere
        var extra = new PlanSource
        {
            SourceId = unused.StockId, Kind = SourceKind.WarehouseRemnant, StockId = unused.StockId, StockVersion = unused.Version,
            WidthMm = unused.WidthMm, LengthMm = unused.LengthMm, PatternAxis = unused.PatternAxis,
            DisplayWidthMm = unused.WidthMm, DisplayLengthMm = unused.LengthMm, AreaM2 = unused.WidthMm * unused.LengthMm / 1_000_000m,
        };
        Assert.Contains("UNUSED_SOURCE", T.Codes(Check(job, plan with { Sources = [.. plan.Sources, extra] })));
    }

    [Fact]
    public void Tree_cycles_and_orphans_are_detected()
    {
        var (job, plan) = MixedPlan();
        var tree = plan.CutTrees.First(t => t.Nodes.Count(n => n.Kind == CutNodeKind.Cut) >= 2);
        var cut = tree.Nodes.Last(n => n.Kind == CutNodeKind.Cut);
        var cyclic = tree with { Nodes = tree.Nodes.Select(n => n == cut ? n with { SecondChildId = tree.RootNodeId } : n).ToList() };
        var codes = T.Codes(Check(job, plan with { CutTrees = plan.CutTrees.Select(t => t == tree ? cyclic : t).ToList() })).ToList();
        Assert.Contains("TREE_CYCLE", codes);
        Assert.Contains("TREE_ORPHAN", codes);
    }

    [Fact]
    public void Purchase_and_cut_step_tampering_are_detected()
    {
        var (job, plan) = MixedPlan();
        Assert.Contains("PURCHASE_INVALID", T.Codes(Check(job, plan with { Purchase = plan.Purchase! with { PurchaseCost = 0 } })));
        Assert.Contains("PURCHASE_INVALID", T.Codes(Check(job, plan with { Purchase = null })));
        var reversed = plan.CutSteps.Reverse().Select((s, i) => s with { Sequence = i + 1 }).ToList();
        Assert.Contains("CUT_STEPS_INVALID", T.Codes(Check(job, plan with { CutSteps = reversed })));
        Assert.Contains("UNVERIFIABLE_CLAIM", T.Codes(Check(job, plan with { QualityStatus = QualityStatus.OptimalProven })));
    }

    [Fact]
    public void Forged_cut_out_size_or_padding_breakdown_is_rejected()
    {
        var (job, plan) = MixedPlan();
        var b = plan.Placements.First(p => p.PieceId == "B");
        var wrongCut = plan with { Placements = plan.Placements.Select(p => p == b ? p with { CutWidthMm = p.NominalWidthMm } : p).ToList() };
        Assert.Contains("CUTOUT_SIZE_MISMATCH", T.Codes(Check(job, wrongCut)));

        var pu = plan.Purchase!;
        var wrongSplit = plan with { Purchase = pu with { PaddingLengthMm = pu.PaddingLengthMm + 1, LengthWithoutPaddingMm = pu.LengthWithoutPaddingMm - 1 } };
        Assert.Contains("PURCHASE_INVALID", T.Codes(Check(job, wrongSplit)));
    }

    [Fact]
    public void Pinwheel_arrangement_admits_no_guillotine_tree_and_is_rejected()
    {
        // Five rectangles tiling 300×300 with no edge-to-edge first cut.
        GuillotineSeparability.Box[] pinwheel =
        [
            new(0, 0, 200, 100), new(200, 0, 100, 200), new(100, 200, 200, 100), new(0, 100, 100, 200), new(100, 100, 100, 100),
        ];
        Assert.False(GuillotineSeparability.IsGuillotine(pinwheel, 300, 300));
        GuillotineSeparability.Box[] stacked = [new(0, 0, 200, 100), new(0, 100, 200, 100), new(0, 200, 200, 100), new(200, 0, 100, 100), new(200, 100, 100, 200)];
        Assert.True(GuillotineSeparability.IsGuillotine(stacked, 300, 300));

        // A real plan for the same pieces exists (guillotine), but forcing the pinwheel positions into it must fail.
        var job = T.Job([T.Piece("D", 200, 100, q: 4), T.Piece("S", 100, 100)], stock: [T.Stock("R", 300, 300)], directional: false);
        var plan = T.Best(T.RunOk(job));
        Assert.False(plan.RequiresPurchase);
        var dominoes = plan.Placements.Where(p => p.PieceId == "D").ToList();
        var forced = plan.Placements.Select(p =>
        {
            int i = dominoes.IndexOf(p);
            var box = p.PieceId == "S" ? pinwheel[4] : pinwheel[i];
            bool rot = box.W == 100 && box.H == 200;
            var rect = new RectMm(box.X, box.Y, box.W, box.H);
            return p with { Footprint = rect, NominalRect = rect, PieceRotated = rot, PieceRotationDegrees = rot ? 90 : 0 };
        }).ToList();
        var outcome = Check(job, plan with { Placements = forced });
        Assert.False(outcome.Passed);
        Assert.Contains("LEAF_MISMATCH", T.Codes(outcome));
        Assert.DoesNotContain("OVERLAP", T.Codes(outcome));
    }

    [Fact]
    public void Validation_survives_a_json_round_trip()
    {
        var (job, _) = MixedPlan();
        var r = T.RunOk(job);
        var back = OptimizerJson.Deserialize<OptimizationResult>(OptimizerJson.Serialize(r));
        var jobBack = OptimizerJson.Deserialize<OptimizationSnapshot>(OptimizerJson.Serialize(job));
        foreach (var c in back.Candidates) Assert.True(Validator.Validate(jobBack, c).Passed);
        Assert.Equal(r.ResultHash, OptimizerJson.Hash(back.Candidates));
    }

    [Fact]
    public void Garbage_input_produces_failures_not_exceptions()
    {
        var (job, plan) = MixedPlan();
        var outcome = Check(job, plan with { Sources = null!, Placements = null! });
        Assert.False(outcome.Passed);
        var broken = plan with { CutTrees = [new SourceCutTree { SourceId = plan.Sources[0].SourceId, RootNodeId = "x", Nodes = [null!] }] };
        Assert.False(Check(job, broken).Passed);
    }
}

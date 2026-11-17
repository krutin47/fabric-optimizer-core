using DreamGlass.Optimizer.Approval;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Solver;
using DreamGlass.Optimizer.Infrastructure;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Spec §10 UAT fixture table.</summary>
public class AcceptanceTests
{
    [Fact]
    public void Exact_fit_has_zero_residual_and_a_single_leaf_tree()
    {
        var job = T.Job([T.Piece("P1", 600, 900, o: PatternOrientation.Vertical)], stock: [T.Stock("R1", 600, 900)]);
        var best = T.Best(T.RunOk(job));
        Assert.False(best.RequiresPurchase);
        Assert.Equal(0m, best.Metrics.TotalResidualAreaM2);
        Assert.Equal(best.Metrics.ExistingStockAreaConsumedM2, best.Metrics.RequiredCutAreaM2);
        Assert.Equal(0, best.Metrics.TotalCuts);
        var node = Assert.Single(Assert.Single(best.CutTrees).Nodes);
        Assert.Equal(CutNodeKind.Piece, node.Kind);
        Assert.Empty(best.CutSteps);
    }

    [Fact]
    public void Quantity_expands_into_distinct_instances_without_duplicates_or_omissions()
    {
        var best = T.Best(T.RunOk(T.Job([T.Piece("P", 500, 500, q: 3)], [T.Roll("r", 2800)])));
        Assert.Equal(["P#1", "P#2", "P#3"], best.Placements.Select(p => p.PieceInstanceId).Order());
    }

    [Fact]
    public void Warehouse_only_plan_has_no_purchase_order()
    {
        var job = T.Job([T.Piece("P", 500, 400, q: 2, o: PatternOrientation.Vertical)], stock: [T.Stock("R1", 1200, 1000), T.Stock("R2", 3000, 3000)]);
        var best = T.Best(T.RunOk(job));
        Assert.False(best.RequiresPurchase);
        Assert.Null(best.Purchase);
        Assert.Equal(0m, best.Metrics.PurchasedAreaM2);
        Assert.Equal(0m, best.Metrics.PurchaseCost);
        // Untouched stock is not counted as consumed.
        Assert.Single(best.Sources);
        Assert.Equal(best.Sources.Single().AreaM2, best.Metrics.ExistingStockAreaConsumedM2);
    }

    [Fact]
    public void Whole_metre_rounding_creates_a_full_width_surplus_offcut()
    {
        var job = T.Job([T.Piece("P", 2800, 24_450, o: PatternOrientation.Vertical)], [T.Roll("roll-2800", 2800, price: 12.5m)]);
        var best = T.Best(T.RunOk(job));
        var p = best.Purchase!;
        Assert.Equal(25m, p.OrderedLengthMetres);
        Assert.Equal(24_450m, p.ConsumedLengthMm);
        Assert.Equal(550m, p.RoundingSurplusMm);
        Assert.Equal(70m, p.PurchasedAreaM2);
        Assert.Equal(875m, p.PurchaseCost);
        var surplus = Assert.Single(best.PredictedResiduals, r => r.RoundingSurplus);
        Assert.Equal(new RectMm(0, 24_450, 2800, 550), surplus.Region);
        Assert.True(surplus.Eligible);
        Assert.Equal(1, best.Metrics.TotalCuts);
        Assert.Equal(CutAxis.Horizontal, best.CutSteps.Single().Axis);
    }

    [Theory]
    [InlineData(16_500, 90, 16_680, 17)]   // 16,500 + 180 padding = 16,680 mm → 17 m
    [InlineData(1_650, 9, 1_668, 2)]       // 1,650 + 18 padding = 1,668 mm → 2 m (always round up)
    [InlineData(2_000, 0, 2_000, 2)]       // exact metre, no padding, no surplus
    public void Order_reports_length_without_padding_plus_padding_and_rounds_up(int length, int pad, int needed, int metres)
    {
        var job = T.Job(
            [T.Piece("Curtain", 1200, length, o: PatternOrientation.Vertical, padding: new Padding { Top = pad, Bottom = pad })],
            [T.Roll("roll-2800", 2800)]);
        var p = T.Best(T.RunOk(job)).Purchase!;
        Assert.Equal(needed, p.ConsumedLengthMm);
        Assert.Equal(length, p.LengthWithoutPaddingMm);
        Assert.Equal(2 * pad, p.PaddingLengthMm);
        Assert.Equal(metres, p.OrderedLengthMetres);
        Assert.Equal(metres * 1000 - needed, p.RoundingSurplusMm);
    }

    [Fact]
    public void Padding_share_follows_the_arrangement_and_always_adds_up()
    {
        // Three 1000 mm wide panels on a 2800 roll: two side by side, one below → two padded rows along the roll.
        var job = T.Job(
            [T.Piece("P", 1000, 800, q: 3, o: PatternOrientation.Vertical, padding: new Padding { Top = 5, Bottom = 5 })],
            [T.Roll("roll-2800", 2800)]);
        var best = T.Best(T.RunOk(job));
        var p = best.Purchase!;
        Assert.Equal(p.ConsumedLengthMm, p.LengthWithoutPaddingMm + p.PaddingLengthMm);
        Assert.Equal(1620m, p.ConsumedLengthMm);
        Assert.Equal(20m, p.PaddingLengthMm);
        Assert.Equal(2m, p.OrderedLengthMetres);
    }

    [Fact]
    public void Placements_report_the_full_cut_out_size_including_padding()
    {
        var job = T.Job(
            [T.Piece("P2", 450, 700, padding: new Padding { Top = 5, Right = 4, Bottom = 6, Left = 3 })],
            [T.Roll("roll-2800", 2800)]);
        var p = T.Best(T.RunOk(job)).Placements.Single();
        Assert.Equal((457m, 711m), (p.CutWidthMm, p.CutLengthMm)); // 450 + 3 + 4, 700 + 5 + 6 — independent of rotation
        Assert.Equal((450m, 700m), (p.NominalWidthMm, p.NominalLengthMm));
    }

    [Fact]
    public void Roll_printout_shows_the_order_and_cut_out_sizes()
    {
        var job = T.Job(
            [T.Piece("P1", 600, 900, q: 2, o: PatternOrientation.Vertical, pad: 2), T.Piece("P2", 450, 700, pad: 5)],
            [T.Roll("roll-2800", 2800)]);
        var best = T.Best(T.RunOk(job));
        var svg = DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(best, true).Single().Svg;
        Assert.Contains("ORDER FROM SUPPLIER: 1 m of roll-2800 (2,800 mm high)", svg);
        // 96 × 2,800 is below the 200 mm minimum short side, so it is scrap, not warehouse stock.
        Assert.Contains("Layout needs 904 mm (900 + 4 padding) → rounded up to whole metres. The 96 mm left over is scrap: too small to keep (short side 96 mm &lt; minimum 200 mm).", svg);
        Assert.Contains("Cut-out 604 × 904 mm", svg);
        Assert.Contains("finished 600 × 900 · pad 2", svg);
        Assert.Contains("Cut-out 460 × 710 mm", svg);
        Assert.Contains("needed 904", svg);
        Assert.Contains("ordered 1,000", svg);
    }

    [Fact]
    public void Order_line_sends_the_leftover_to_the_warehouse_only_when_it_is_big_enough_to_keep()
    {
        // 600 mm used of 1 m: the 400 × 2,800 mm leftover is kept.
        var job = T.Job([T.Piece("P1", 600, 900, q: 2, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800, maxMetres: 50) with { PatternAxis = PatternAxis.Width }]);
        var svg = DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(T.Best(T.RunOk(job)), true).Single().Svg;
        Assert.Contains("The 400 mm left over goes to the warehouse.", svg);
    }

    [Fact]
    public void Printouts_show_the_solver_and_checker_versions_in_the_footer()
    {
        var offer = T.Roll("roll-2800", 2800, maxMetres: 50) with { PatternAxis = PatternAxis.Width };
        var job = T.Job([T.Piece("P1", 600, 900, q: 2, o: PatternOrientation.Vertical)], [offer]);
        var result = T.RunOk(job);
        var best = T.Best(result);
        string footer = $"Plan {best.CandidateId} · DreamGlass {HybridCutOptimizer.BuildVersion} · solver {result.SolverVersion} · checker {best.Validation.ValidatorVersion}";
        Assert.Equal(HybridCutOptimizer.SolverVersion, result.SolverVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+$", HybridCutOptimizer.BuildVersion);
        Assert.Contains(footer, DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(best, true, result.SolverVersion).Single().Svg);
        Assert.Contains(footer, DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.RenderSupplierRollOverview(best, offer, result.SolverVersion));
        // Without the run's version the printout names this library's solver version.
        Assert.Contains($"solver {HybridCutOptimizer.SolverVersion} · checker", DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(best, true).Single().Svg);
    }

    [Fact]
    public void A_row_of_panels_sets_the_section_length_as_in_the_workshop_sketch()
    {
        // Workshop sketch (12 Nov 2026), 1,400 mm roll: two 1,000 × 1,400 panels, then three 510 mm cut-outs side by side
        // (1,530 mm) over a 1,440 × 540 cut-out with a 90 mm offcut beside it. Uses 3,530 mm of the 4 m ordered and
        // leaves one 470 × 1,400 piece to keep. Before row sections the solver used 3,950 mm and left 510 × 590 and
        // 420 × 860, because every section was only as long as one piece.
        var job = T.Job(
            [
                T.Piece("P", 1000, 1400, q: 2, o: PatternOrientation.Vertical),
                T.Piece("P2", 800, 500, q: 3, o: PatternOrientation.Horizontal, pad: 5),
                T.Piece("P3", 1400, 500, o: PatternOrientation.Vertical, pad: 20),
            ],
            [
                T.Roll("roll-2800", 2800, price: 12.5m, maxMetres: 50) with { PatternAxis = PatternAxis.Width },
                T.Roll("roll-1400", 1400, price: 9m, maxMetres: 50) with { PatternAxis = PatternAxis.Width },
            ]);
        var best = T.Best(T.RunOk(job, T.Det(work: 2500)));
        Assert.Equal("roll-1400", best.Purchase!.RollOfferId); // €50.40 beats 2 m of roll-2800 at €70
        Assert.Equal(4m, best.Purchase.OrderedLengthMetres);
        Assert.Equal(3530m, best.Purchase.ConsumedLengthMm);
        var keep = Assert.Single(best.PredictedResiduals, r => r.Eligible);
        Assert.Equal((470m, 1400m), (keep.Region.Length, keep.Region.Width));
        Assert.Equal(8, best.Metrics.TotalCuts);
    }

    [Fact]
    public void Panels_stand_upright_across_the_roll_height_on_a_left_to_right_sheet()
    {
        // The workshop's reference layout: the roll is drawn unrolled left to right with its 2,800 mm height top to
        // bottom; the pattern runs across the roll, so two 600 × 900 panels stand upright, one above the other, and only
        // 600 mm of running length is used → 1 m is ordered.
        var job = T.Job(
            [T.Piece("P1", 600, 900, q: 2, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800, maxMetres: 50) with { PatternAxis = PatternAxis.Width }]);
        var best = T.Best(T.RunOk(job));
        Assert.Equal(600m, best.Purchase!.ConsumedLengthMm);
        Assert.Equal(1m, best.Purchase.OrderedLengthMetres);
        var roll = Assert.Single(best.Sources);
        Assert.Equal(270, roll.DisplayRotationDegrees);
        Assert.Equal((1000m, 2800m), (roll.DisplayWidthMm, roll.DisplayLengthMm));
        Assert.All(best.Placements, p => Assert.True(p.PieceRotated)); // height across the roll = upright on the sheet
        var svg = DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(best, true).Single().Svg;
        Assert.Contains("ORDER FROM SUPPLIER: 1 m of roll-2800 (2,800 mm high)", svg);
        Assert.Contains("needed 600", svg);
        Assert.Contains("ordered 1,000", svg);
        Assert.DoesNotContain("turned 90°", svg);
    }

    [Fact]
    public void Printout_reads_the_cut_out_size_from_the_geometry_not_the_reported_fields()
    {
        var job = T.Job([T.Piece("P2", 450, 700, pad: 5), T.Piece("Q", 300, 400)], [T.Roll("roll-2800", 2800)]);
        var best = T.Best(T.RunOk(job));
        // A result produced before v1.1 (or hand-edited) has no cut-out fields: the printout must still be right.
        var stale = best with { Placements = best.Placements.Select(p => p with { CutWidthMm = 0, CutLengthMm = 0 }).ToList() };
        var svg = DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(stale, true).Single().Svg;
        Assert.Contains("Cut-out 460 × 710 mm", svg);
        Assert.Contains("300 × 400 mm", svg);
        Assert.DoesNotContain(" 0 × 0 ", svg);
    }

    [Fact]
    public void Supplier_roll_length_caps_the_order_and_only_the_ordered_metres_are_shown()
    {
        // The user's example: the layout uses 14,450 mm of a 31 m supplier roll, so 15 m is ordered. The other 16 m stay
        // with the supplier: they are not bought, not drawn and not offered as a reusable remnant.
        var job = T.Job([T.Piece("Curtain", 1400, 14_450, q: 2, o: PatternOrientation.Vertical)], [T.Roll("roll-2800", 2800, maxMetres: 31)]);
        var best = T.Best(T.RunOk(job));
        Assert.Equal(15m, best.Purchase!.OrderedLengthMetres);
        Assert.Equal(14_450m, best.Purchase.ConsumedLengthMm);
        var roll = Assert.Single(best.Sources);
        Assert.Equal(15_000m, roll.LengthMm);
        Assert.All(best.PredictedResiduals, r => Assert.True(r.Region.Y + r.Region.Length <= 15_000m));
        var surplus = Assert.Single(best.PredictedResiduals);
        Assert.True(surplus.RoundingSurplus);
        Assert.Equal(550m, surplus.Region.Length);
        var svg = DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(best, true).Single().Svg;
        Assert.Contains("ORDER FROM SUPPLIER: 15 m of roll-2800", svg);
        Assert.Contains("ordered 15,000", svg);
        Assert.DoesNotContain("31,000", svg);
    }

    [Fact]
    public void Full_roll_view_shows_the_whole_supplier_roll_the_ordered_part_and_where_the_supplier_cuts()
    {
        var offer = T.Roll("roll-2800", 2800, maxMetres: 31) with { PatternAxis = PatternAxis.Width };
        var job = T.Job([T.Piece("Curtain", 7225, 2600, q: 2, o: PatternOrientation.Vertical)], [offer]);
        var best = T.Best(T.RunOk(job));
        Assert.Equal(15m, best.Purchase!.OrderedLengthMetres);
        var svg = DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.RenderSupplierRollOverview(best, offer);
        Assert.NotNull(svg);
        Assert.Contains("SUPPLIER ROLL: roll-2800 · 31 m × 2,800 mm high", svg);
        Assert.Contains("The supplier cuts 15 m from this roll for you. The other 16 m stay with the supplier and are not bought.", svg);
        Assert.Contains("supplier cuts here · 15 m", svg);
        Assert.Contains("stays with the supplier · 16 m not bought", svg);
        Assert.Contains("ordered 15 m", svg);
        Assert.Contains("supplier roll 31 m", svg);
        Assert.Contains(">10 m</text>", svg); // metre ruler
        Assert.Contains("Curtain#1", svg);

        // No roll length on the offer, or nothing ordered: there is no full-roll view.
        Assert.Null(DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.RenderSupplierRollOverview(best, offer with { MaxOrderMetres = null }));
        var own = T.Best(T.RunOk(T.Job([T.Piece("P", 500, 500)], stock: [T.Stock("R1", 1000, 1000)])));
        Assert.Null(DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.RenderSupplierRollOverview(own, offer));
    }

    [Fact]
    public void A_whole_roll_already_in_the_warehouse_keeps_its_remainder_as_a_reusable_remnant()
    {
        var job = T.Job(
            [T.Piece("Curtain", 1400, 14_450, q: 2, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800, maxMetres: 31)],
            stock: [T.Stock("ROLL-31", 2800, 31_000)]);
        var best = T.Best(T.RunOk(job));
        Assert.Null(best.Purchase);
        var rest = Assert.Single(best.PredictedResiduals, r => r.Eligible);
        Assert.Equal((2800m, 16_550m), (rest.Region.Width, rest.Region.Length));
        Assert.False(rest.RoundingSurplus);
        var svg = DreamGlass.Optimizer.Printing.CutPlanSvgRenderer.Render(best, true).Single().Svg;
        Assert.Contains("FROM WAREHOUSE: ROLL-31", svg);
        Assert.Contains("reusable 16,550×2,800", svg); // as drawn: running length × height
    }

    [Fact]
    public void Fixed_stock_mode_keeps_the_given_length_without_rounding()
    {
        var job = T.Job([T.Piece("P", 1400, 3000, q: 3, o: PatternOrientation.Vertical)], [T.Roll("fixed", 2800, fixedLength: 25_000)]);
        var best = T.Best(T.RunOk(job));
        Assert.True(best.Purchase!.FixedLength);
        Assert.Equal(25m, best.Purchase.OrderedLengthMetres);
        Assert.Equal(6000m, best.Purchase.ConsumedLengthMm);
    }

    [Fact]
    public void Supplier_increment_and_minimum_are_respected()
    {
        var job = T.Job([T.Piece("P", 2800, 1200, o: PatternOrientation.Vertical)], [T.Roll("r", 2800, minMetres: 3, increment: 2)]);
        Assert.Equal(3m, T.Best(T.RunOk(job)).Purchase!.OrderedLengthMetres);
        var longer = T.Job([T.Piece("P", 2800, 3200, o: PatternOrientation.Vertical)], [T.Roll("r", 2800, minMetres: 3, increment: 2)]);
        Assert.Equal(5m, T.Best(T.RunOk(longer)).Purchase!.OrderedLengthMetres);
    }

    [Fact]
    public void Alternative_roll_widths_are_compared_and_the_tradeoff_is_retained()
    {
        var job = T.Job(
            [T.Piece("P", 1400, 1000, q: 2, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800, price: 12.50m), T.Roll("roll-1400", 1400, price: 9.00m)]);
        var r = T.RunOk(job);
        var best = T.Best(r);
        // Equal purchased area (2.8 m²) and no scrap → lower cost wins under proposed-v1.
        Assert.Equal("roll-1400", best.Purchase!.RollOfferId);
        Assert.Equal(2m, best.Purchase.OrderedLengthMetres);
        Assert.Equal(25.2m, best.Metrics.PurchaseCost);
        var alt = r.Candidates.First(c => c.Purchase?.RollOfferId == "roll-2800");
        Assert.Equal(2.8m, alt.Metrics.PurchasedAreaM2);
        Assert.Equal(35m, alt.Metrics.PurchaseCost);
    }

    [Fact]
    public void Joint_search_beats_greedy_remnants_first_assignment()
    {
        var job = T.Job(
            [T.Piece("S", 500, 500, o: PatternOrientation.Vertical), T.Piece("L", 1000, 1000, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800)],
            [T.Stock("A", 1000, 1000), T.Stock("B", 600, 600)]);

        // Naive baseline: pieces in listed order, each into the first remnant that fits, else the roll.
        var p = Normalizer.Normalize(job);
        var naive = PackingState.Create(p, [0, 1, 2], "naive");
        var policy = new PlacementPolicy(TierOrder.RemnantsFirst, OpenPreference.NoPreference, FitRule.BottomLeft, SplitRule.HorizontalFirst, false);
        Assert.True(Construction.PlaceAll(naive, [0, 1], policy, new Rng(1)));
        Assert.Equal(2, naive.LayoutOf[1]); // instance 1 (L) was forced onto the new roll (layout 2): a purchase

        var best = T.Best(T.RunOk(job));
        Assert.False(best.RequiresPurchase);
        Assert.Equal("B", best.Placements.Single(x => x.PieceId == "S").SourceId);
        Assert.Equal("A", best.Placements.Single(x => x.PieceId == "L").SourceId);
    }

    [Fact]
    public void One_large_offcut_is_preferred_to_skinny_strips()
    {
        var job = T.Job([T.Piece("P", 1000, 300, q: 2, o: PatternOrientation.Vertical)], stock: [T.Stock("R1", 1000, 1000)]);
        var best = T.Best(T.RunOk(job));
        var residual = Assert.Single(best.PredictedResiduals);
        Assert.Equal((1000m, 400m), (residual.Region.Width, residual.Region.Length));
        Assert.True(residual.Eligible);
        Assert.Equal(0m, best.Metrics.FragmentationIndex);
        Assert.Equal(0, best.Metrics.ThinFragmentCount);
    }

    [Fact]
    public void Small_case_is_solved_with_a_proven_purchase_bound()
    {
        var job = T.Job([T.Piece("P", 700, 1000, q: 8, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)]);
        var r = T.RunOk(job);
        Assert.True(r.Diagnostics.Exact!.Completed);
        Assert.Equal(5.6m, r.Diagnostics.Exact.ProvenMinPurchasedAreaM2);
        var best = T.Best(r);
        Assert.Equal(QualityStatus.BoundedGap, best.QualityStatus);
        Assert.Equal(0m, best.Bound!.GapM2);
        Assert.Equal("ExactGuillotineDP", best.Bound.Method);
        Assert.Contains("Tier 1 only", best.Bound.Scope);
    }

    [Fact]
    public void Determinism_same_input_seed_and_version_give_identical_results()
    {
        var job = T.Job(
            [T.Piece("A", 430, 610, q: 5, o: PatternOrientation.Vertical), T.Piece("B", 900, 350, q: 4), T.Piece("C", 1200, 800, q: 2, o: PatternOrientation.Horizontal)],
            [T.Roll("roll-2800", 2800), T.Roll("roll-1600", 1600, price: 11m)],
            [T.Stock("R1", 1000, 1500), T.Stock("R2", 800, 2000, PatternAxis.Width)]);
        var a = T.RunOk(job, T.Det(work: 400));
        var b = T.RunOk(job, T.Det(work: 400));
        Assert.True(a.Diagnostics.Reproducible);
        Assert.Equal(a.ResultHash, b.ResultHash);
        Assert.Equal(a.Candidates.Select(c => c.CandidateId), b.Candidates.Select(c => c.CandidateId));
        Assert.Equal(a.InputHash, b.InputHash);
        Assert.NotEqual(a.RunId, b.RunId);
    }

    [Fact]
    public void Precancelled_run_returns_cancelled_without_partial_plans()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = T.Run(T.Job([T.Piece("P", 500, 500, q: 4)], [T.Roll("r", 2800)]), ct: cts.Token);
        Assert.Equal(RunStatus.Cancelled, r.Status);
        Assert.Empty(r.Candidates);
    }

    [Fact]
    public void Cancellation_mid_search_preserves_the_valid_incumbent()
    {
        var job = Fixtures.Random(new Random(5), instances: 120) with { Search = new SearchSettings { TimeBudgetMs = 60_000, Seed = 3 } };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var r = T.Run(job, new OptimizationOptions { StagnationLimit = 1_000_000 }, cts.Token);
        Assert.Equal(RunStatus.Succeeded, r.Status);
        Assert.Equal(StopReason.Cancelled, r.StopReason);
        Assert.True(r.ElapsedMs < 10_000);
        T.AllValid(r, job);
    }

    [Fact]
    public void Tiny_time_budget_never_returns_an_unvalidated_plan()
    {
        var job = Fixtures.Random(new Random(9), instances: 300) with { Search = new SearchSettings { TimeBudgetMs = 1, Seed = 1 } };
        var r = T.Run(job, new OptimizationOptions());
        Assert.Contains(r.Status, new[] { RunStatus.TimedOutWithoutPlan, RunStatus.Succeeded });
        T.AllValid(r, job);
    }

    [Fact]
    public void Exhaustive_search_proves_infeasibility_distinct_from_timeout()
    {
        // Area fits (0.72 ≤ 1.0 m²) but two 600×600 squares cannot share a 1000×1000 remnant.
        var job = T.Job([T.Piece("P", 600, 600, q: 2)], stock: [T.Stock("R", 1000, 1000)]);
        var r = T.Run(job);
        Assert.Equal(RunStatus.NoFeasiblePlan, r.Status);
        Assert.True(r.Diagnostics.Exact!.ProvenInfeasible);

        var noExact = T.Run(job, T.Det() with { Exact = new ExactSolverOptions { Enabled = false } });
        Assert.Equal(RunStatus.NoPlanFound, noExact.Status);

        var byArea = T.Run(T.Job([T.Piece("P", 400, 400, q: 2)], stock: [T.Stock("R", 500, 500)]));
        Assert.Equal(RunStatus.NoFeasiblePlan, byArea.Status);
        Assert.Contains(byArea.Diagnostics.InfeasibilityReasons, s => s.Contains("exceeds total remnant area"));
    }

    [Fact]
    public void Employee_can_approve_a_lower_ranked_candidate_with_an_audited_reason()
    {
        var job = T.Job(
            [T.Piece("P", 1400, 1000, q: 2, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800, price: 12.50m), T.Roll("roll-1400", 1400, price: 9.00m)]);
        var r = T.RunOk(job);
        var second = r.Candidates.Single(c => c.Rank == 2);
        var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => PlanSelection.Approve(r, job, second.CandidateId, "emp-42", null, now));
        var approval = PlanSelection.Approve(r, job, second.CandidateId, "emp-42", "Wider roll already on the cutting table", now);
        Assert.Equal(second.CandidateId, approval.CandidateId);
        Assert.Equal(2, approval.CandidateRank);
        Assert.False(approval.IsTopRanked);
        Assert.Equal(second.Purchase, approval.Purchase);
        Assert.Equal(r.RunId, approval.RunId);
        Assert.Equal(64, approval.CandidateContentHash.Length);

        var forged = r with { Candidates = r.Candidates.Select(c => c == second ? c with { Metrics = c.Metrics with { TotalCuts = 0 } } : c).ToList() };
        Assert.Throws<InvalidOperationException>(() => PlanSelection.Approve(forged, job, second.CandidateId, "emp-42", "x", now));
    }

    [Fact]
    public void Approval_lists_exact_stock_versions_to_reserve()
    {
        var job = T.Job([T.Piece("P", 500, 400, q: 2)], [T.Roll("r", 2800)], [T.Stock("R1", 1200, 1000, version: 17)]);
        var r = T.RunOk(job);
        var best = T.Best(r);
        var approval = PlanSelection.Approve(r, job, best.CandidateId, "emp-1", null, DateTimeOffset.UnixEpoch);
        Assert.Equal(best.Sources.Where(s => s.Kind == SourceKind.WarehouseRemnant).Select(s => new StockReference(s.StockId!, s.StockVersion!.Value)), approval.StockToReserve);
    }

    [Fact]
    public void Deep_trees_from_many_pieces_validate_and_serialise()
    {
        var job = T.Job([T.Piece("P", 2800, 50, q: 400, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)], timeMs: 60_000);
        var r = T.RunOk(job, T.Det(work: 40, stagnation: 20));
        var best = T.Best(r);
        Assert.Equal(20m, best.Purchase!.OrderedLengthMetres);
        Assert.Equal(0m, best.Metrics.TotalResidualAreaM2);
        Assert.NotEmpty(OptimizerJson.Serialize(r));
    }
}

using DreamGlass.Optimizer.Approval;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Printing;
using DreamGlass.Optimizer.Solver;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Tests;

/// <summary>
/// Production Engineering Handbook v1.1 acceptance tests for the optimiser library: the Appendix A1 test names that
/// apply to it (kept verbatim), and the §30, §32, §36, §38 and §95–98 contracts. Tenant, database and UI tests of A1
/// belong to the host application; docs/requirements/ledger.csv lists which ones and where.
/// </summary>
public class HandbookTests
{
    private static readonly CutPlanValidator Validator = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static (OptimizationSnapshot Job, OptimizationResult Result, CandidateCutPlan Plan) MixedPlan()
    {
        var job = T.Job(
            [T.Piece("A", 400, 600, q: 2, o: PatternOrientation.Vertical), T.Piece("B", 300, 300, pad: 5), T.Piece("C", 2000, 700, o: PatternOrientation.Vertical)],
            [T.Roll("roll-2800", 2800, price: 12.5m)],
            [T.Stock("R1", 1000, 1000), T.Stock("R2", 500, 400), T.Stock("R3", 300, 300)]);
        var r = T.RunOk(job);
        var plan = r.Candidates.First(c => c.Sources.Count >= 2 && c.RequiresPurchase);
        return (job, r, plan);
    }

    private static IEnumerable<string> Codes(PlanValidationOutcome o) => o.Failures.Select(f => f.Code);

    // ---- Appendix A1 -------------------------------------------------------------------------------------------

    [Fact]
    public void InvalidGuillotineCut_CannotBePublished()
    {
        var (job, r, plan) = MixedPlan();
        var tree = plan.CutTrees.First(t => t.Nodes.Count(n => n.Kind == CutNodeKind.Cut) > 0);
        var cut = tree.Nodes.First(n => n.Kind == CutNodeKind.Cut);
        var forgedTree = tree with { Nodes = tree.Nodes.Select(n => n == cut ? n with { PositionMm = n.PositionMm + 1 } : n).ToList() };
        var forged = plan with { CutTrees = plan.CutTrees.Select(t => t == tree ? forgedTree : t).ToList() };

        var outcome = Validator.Validate(job, forged);
        Assert.False(outcome.Passed);
        Assert.Contains(Codes(outcome), c => c is "CHILD_REGION_MISMATCH" or "ILLEGAL_CUT" or "CUT_STEPS_INVALID");

        // Approval re-validates, so the forged plan cannot be approved even though it still says it passed.
        var run = r with { Candidates = [forged with { Rank = 1 }] };
        var refused = Assert.Throws<PlanApprovalException>(() => PlanSelection.Approve(run, job, forged.CandidateId, "emp-1", null, Now));
        Assert.Equal("CANDIDATE_INVALID", refused.Code);

        // And nothing prints a plan that has not passed.
        var unchecked_ = forged with { Validation = new PlanValidationOutcome() };
        Assert.Throws<InvalidOperationException>(() => CutPlanDocument.Render(unchecked_, job));
        Assert.Throws<InvalidOperationException>(() => CutPlanSvgRenderer.Render(unchecked_, true));
    }

    [Fact]
    public void UnconstrainedPieceRotation_StillPreservesActualPatternRules()
    {
        // "None" keeps both rotations open: a 1,500 × 600 panel only fits a 1,000 mm roll turned.
        var job = T.Job(
            [T.Piece("Free", 1500, 600, q: 2, o: PatternOrientation.None),
             T.Piece("Up", 400, 900, q: 2, o: PatternOrientation.Vertical),
             T.Piece("Across", 900, 300, q: 1, o: PatternOrientation.Horizontal)],
            [T.Roll("r", 1000) with { PatternAxis = PatternAxis.Length }],
            [T.Stock("W", 1200, 1200, PatternAxis.Width)]);
        var r = T.RunOk(job);
        foreach (var c in r.Candidates)
        {
            foreach (var p in c.Placements)
            {
                var src = c.Sources.Single(s => s.SourceId == p.SourceId);
                Assert.Equal(SheetTransform.FinishedPattern(src.PatternAxis, p.PieceRotated), p.FinishedPatternDirection);
                if (p.RequiredOrientation != PatternOrientation.None) Assert.Equal(p.RequiredOrientation, p.FinishedPatternDirection);
                if (p.PieceId == "Free" && src.Kind == SourceKind.NewRoll) Assert.True(p.PieceRotated, "the wide free panel must be turned to fit the roll");
            }
        }

        // On plain material nothing is reported as a pattern direction.
        var plain = T.RunOk(job with { MaterialDirectional = false });
        Assert.All(plain.Candidates.SelectMany(c => c.Placements), p => Assert.Equal(PatternOrientation.None, p.FinishedPatternDirection));
    }

    [Fact]
    public void PurchaseRoundUp_PreservesFullWidthRemainder()
    {
        // The handbook's example: 24.45 m used → 25 m ordered, and the 0.55 m full-width remainder is a predicted
        // remnant, never hidden in "utilisation".
        var job = T.Job([T.Piece("Long", 1000, 24450, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)]);
        var best = T.Best(T.RunOk(job));
        var p = best.Purchase!;
        Assert.Equal(25m, p.OrderedLengthMetres);
        Assert.Equal(24450m, p.ConsumedLengthMm);
        Assert.Equal(550m, p.RoundingSurplusMm);
        var surplus = Assert.Single(best.PredictedResiduals, x => x.RoundingSurplus);
        Assert.Equal(new RectMm(0, 24450, 2800, 550), surplus.Region);
        Assert.True(surplus.Eligible);

        var m = best.Metrics;
        Assert.Equal(70m, m.PurchasedAreaM2);
        Assert.Equal(68.46m, m.NewConsumedAreaM2);
        Assert.Equal(1.54m, m.RoundingSurplusAreaM2);
        Assert.Equal(m.PurchasedAreaM2, m.NewConsumedAreaM2 + m.RoundingSurplusAreaM2);
    }

    [Fact]
    public void StaleJobRevision_DoesNotApproveOldPlan()
    {
        var (job, r, plan) = MixedPlan();
        var live = new LiveJobState { JobRevisionId = "test-2", Stock = job.Stock };
        var refused = Assert.Throws<PlanApprovalException>(() => PlanSelection.Approve(r, job, plan.CandidateId, "emp-1", "trade-off", Now, live: live));
        Assert.Equal("STALE_JOB_REVISION", refused.Code);

        // Against the edited job itself, the old plan fails validation: it was computed from another snapshot.
        var edited = job with { JobRevisionId = "test-2", Pieces = [.. job.Pieces, T.Piece("D", 200, 200)] };
        Assert.Contains("INPUT_VERSION_MISMATCH", Codes(Validator.Validate(edited, plan)));
    }

    // ---- §97–98: approval against the live state ------------------------------------------------------------------

    [Fact]
    public void Changed_or_missing_stock_is_never_swapped_silently()
    {
        var (job, r, plan) = MixedPlan();
        var used = plan.Sources.First(s => s.Kind == SourceKind.WarehouseRemnant);
        var current = new LiveJobState { JobRevisionId = job.JobRevisionId, Stock = job.Stock };
        Assert.NotNull(PlanSelection.Approve(r, job, plan.CandidateId, "emp-1", "trade-off", Now, live: current));

        var bumped = current with { Stock = job.Stock.Select(s => s.StockId == used.StockId ? s with { Version = s.Version + 1 } : s).ToList() };
        Assert.Equal("STALE_STOCK_VERSION", Assert.Throws<PlanApprovalException>(() => PlanSelection.Approve(r, job, plan.CandidateId, "emp-1", "x", Now, live: bumped)).Code);

        var gone = current with { Stock = job.Stock.Where(s => s.StockId != used.StockId).ToList() };
        var refused = Assert.Throws<PlanApprovalException>(() => PlanSelection.Approve(r, job, plan.CandidateId, "emp-1", "x", Now, live: gone));
        Assert.Equal("STOCK_UNAVAILABLE", refused.Code);
        Assert.Contains(used.StockId!, refused.Message);
    }

    [Fact]
    public void Suspended_organisation_cannot_approve_a_plan()
    {
        var (job, r, plan) = MixedPlan();
        var live = new LiveJobState { OrganizationActive = false, JobRevisionId = job.JobRevisionId, Stock = job.Stock };
        Assert.Equal("ORGANIZATION_INACTIVE", Assert.Throws<PlanApprovalException>(() => PlanSelection.Approve(r, job, plan.CandidateId, "emp-1", "x", Now, live: live)).Code);
    }

    [Fact]
    public void Stock_of_another_organisation_is_rejected_by_input_check_and_validator()
    {
        var job = T.Job([T.Piece("P", 500, 400)], [T.Roll("r", 2800)], [T.Stock("R1", 1200, 1000)]) with { OrganizationId = "org-a" };
        var foreign = job with { Stock = [job.Stock[0] with { OrganizationId = "org-b" }] };
        var r = T.Run(foreign);
        Assert.Equal(RunStatus.InvalidInput, r.Status);
        Assert.Contains(r.InputErrors, e => e.Code == "STOCK_ORGANIZATION_MISMATCH" && e.Path == "$.stock[0].organizationId");

        // A stock row with an organisation is also refused when the job has none (no implicit tenant).
        Assert.Contains(T.Run(job with { OrganizationId = null, Stock = foreign.Stock }).InputErrors, e => e.Code == "STOCK_ORGANIZATION_MISMATCH");

        var own = job with { Stock = [job.Stock[0] with { OrganizationId = "org-a" }] };
        var plan = T.RunOk(own).Candidates.First(c => c.Sources.Any(s => s.Kind == SourceKind.WarehouseRemnant));
        Assert.Equal(own.OrganizationId, T.RunOk(own).OrganizationId);
        Assert.Contains("ORGANIZATION_MISMATCH", Codes(Validator.Validate(foreign, plan)));
    }

    // ---- §97: excluded stock and the snapshot a plan belongs to ---------------------------------------------------

    [Fact]
    public void Excluded_stock_needs_a_reason_and_is_never_used()
    {
        var job = T.Job([T.Piece("P", 500, 400, q: 2)], [T.Roll("r", 2800)], [T.Stock("R1", 1200, 1000)]);
        Assert.Contains(T.Run(job with { ExcludedStock = [new ExcludedStock { StockId = "R2", Version = 3 }] }).InputErrors, e => e.Code == "EXCLUSION_REASON_MISSING");
        Assert.Contains(T.Run(job with { ExcludedStock = [new ExcludedStock { StockId = "R1", Reason = "Damaged" }] }).InputErrors, e => e.Code == "STOCK_EXCLUDED_AND_OFFERED");

        var withNote = job with { ExcludedStock = [new ExcludedStock { StockId = "R2", Version = 3, Reason = "Water stain" }] };
        var r = T.RunOk(withNote);
        Assert.DoesNotContain(r.Candidates.SelectMany(c => c.Sources), s => s.StockId == "R2");

        // A plan that cuts a piece the snapshot excludes is invalid, whatever else is right about it.
        var plan = r.Candidates.First(c => c.Sources.Any(s => s.StockId == "R1"));
        var contradictory = withNote with { ExcludedStock = [.. withNote.ExcludedStock, new ExcludedStock { StockId = "R1", Reason = "Held" }] };
        Assert.Contains("EXCLUDED_STOCK_USED", Codes(Validator.Validate(contradictory, plan)));
    }

    [Fact]
    public void A_plan_is_bound_to_its_snapshot()
    {
        var (job, _, plan) = MixedPlan();
        Assert.Equal(OptimizerJson.SnapshotHash(job), plan.SnapshotHash);
        Assert.Equal(job.JobRevisionId, plan.JobRevisionId);

        var repriced = job with { RollOffers = [job.RollOffers[0] with { PricePerSquareMetre = 13m }] };
        Assert.Contains("INPUT_VERSION_MISMATCH", Codes(Validator.Validate(repriced, plan)));
        Assert.Contains("INPUT_VERSION_MISMATCH", Codes(Validator.Validate(job, plan with { SnapshotHash = "" })));

        // The same measurement written differently (600 vs 600.0) is the same snapshot.
        var rewritten = job with { Pieces = job.Pieces.Select(p => p with { WidthMm = p.WidthMm * 1.0m, LengthMm = p.LengthMm * 1.00m }).ToList() };
        Assert.Equal(OptimizerJson.SnapshotHash(job), OptimizerJson.SnapshotHash(rewritten));
        Assert.True(Validator.Validate(rewritten, plan).Passed);
    }

    // ---- §96: the material ledger ---------------------------------------------------------------------------------

    [Fact]
    public void Material_ledger_balances_and_forged_figures_are_rejected()
    {
        var (job, _, plan) = MixedPlan();
        var m = plan.Metrics;
        decimal offered = job.Stock.Sum(s => s.WidthMm * s.LengthMm) / 1_000_000m;
        Assert.Equal(m.PurchasedAreaM2 + m.ExistingStockAreaConsumedM2, m.RequiredCutAreaM2 + m.ReusableResidualAreaM2 + m.UnusableScrapAreaM2);
        Assert.Equal(m.PurchasedAreaM2, m.NewConsumedAreaM2 + m.RoundingSurplusAreaM2);
        Assert.Equal(offered, m.ExistingStockAreaConsumedM2 + m.ExistingStockUntouchedAreaM2);
        Assert.True(m.ExistingStockUntouchedAreaM2 > 0, "the fixture leaves a remnant untouched; it must not count as waste");
        Assert.True(m.StockDepletionAreaM2 <= m.ExistingStockAreaConsumedM2);

        // Shifting area between two lines keeps the sum but not the recomputed figures.
        var shifted = plan with { Metrics = m with { NewConsumedAreaM2 = m.NewConsumedAreaM2 + 0.01m, RoundingSurplusAreaM2 = m.RoundingSurplusAreaM2 - 0.01m } };
        Assert.Contains("METRIC_MISMATCH", Codes(Validator.Validate(job, shifted)));
        var hidden = plan with { Metrics = m with { ExistingStockUntouchedAreaM2 = 0 } };
        Assert.Contains("LEDGER_MISMATCH", Codes(Validator.Validate(job, hidden)));
    }

    // ---- §31: stable failure identifiers, never an exception ------------------------------------------------------

    [Fact]
    public void Overflowing_coordinates_fail_with_arithmetic_overflow_not_an_exception()
    {
        var (job, _, plan) = MixedPlan();
        var p = plan.Placements[0];
        var huge = plan with { Placements = plan.Placements.Select(x => x == p ? x with { Footprint = x.Footprint with { X = 1e15m } } : x).ToList() };
        var outcome = Validator.Validate(job, huge);
        Assert.False(outcome.Passed);
        Assert.Contains("ARITHMETIC_OVERFLOW", Codes(outcome));
        Assert.DoesNotContain("VALIDATOR_EXCEPTION", Codes(outcome));
    }

    // ---- §32: honest quality claims -------------------------------------------------------------------------------

    [Fact]
    public void Quality_labels_need_the_bound_they_rest_on()
    {
        var (job, _, plan) = MixedPlan();
        var bare = plan with { QualityStatus = QualityStatus.BoundedGap, Bound = null, CostBound = null };
        Assert.Contains("UNVERIFIABLE_CLAIM", Codes(Validator.Validate(job, bare)));
        if (plan.CostBound is { } cb)
        {
            var wrongGap = plan with { CostBound = cb with { GapM2 = cb.GapM2 + 1 } };
            Assert.Contains("UNVERIFIABLE_CLAIM", Codes(Validator.Validate(job, wrongGap)));
        }
    }

    [Fact]
    public void Exact_search_proves_the_cheapest_purchase_when_price_and_area_disagree()
    {
        // roll-1400 needs less fabric (4.2 m²) but costs more per m²; roll-2800 is cheaper (5.6 m² for €70).
        var job = T.Job([T.Piece("P", 1000, 1400, q: 2, o: PatternOrientation.Vertical)], [T.Roll("roll-2800", 2800, price: 12.5m), T.Roll("roll-1400", 1400, price: 18m)]);
        var r = T.RunOk(job);
        var exact = r.Diagnostics.Exact!;
        Assert.True(exact.Completed);
        Assert.Equal(4.2m, exact.ProvenMinPurchasedAreaM2);
        Assert.Equal(70m, exact.ProvenMinPurchaseCost);

        var best = T.Best(r);
        Assert.Equal("roll-2800", best.Purchase!.RollOfferId);
        Assert.Equal(QualityStatus.BoundedGap, best.QualityStatus);
        Assert.True(best.CostBound is { Proven: true, GapM2: 0 });
        Assert.Equal(1.4m, best.Bound!.GapM2);
        Assert.Equal(33.33m, best.Bound.GapPercentOfBound);
        Assert.StartsWith("The price is proven to be the lowest possible", CutPlanDocument.QualityText(best));

        // Ranked by area first, the narrow roll wins and its area is the proven minimum.
        var byArea = T.Best(T.RunOk(job, T.Det() with { Objective = ObjectivePolicy.ProposedV1 }));
        Assert.Equal("roll-1400", byArea.Purchase!.RollOfferId);
        Assert.True(byArea.Bound is { Proven: true, GapM2: 0 });
    }

    [Fact]
    public void Wide_pieces_that_cannot_share_the_roll_width_raise_the_relaxation_bound()
    {
        // Three 1,500 mm panels on a 2,800 mm roll: area alone says 2 m, but no two fit side by side, so 3 m.
        var job = T.Job([T.Piece("W", 1500, 1000, q: 3, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)]);
        var r = T.RunOk(job, T.Det() with { Exact = new ExactSolverOptions { Enabled = false } });
        Assert.Equal(8.4m, r.Diagnostics.PurchasedAreaLowerBoundM2);
        Assert.Equal(Bounds.RelaxationMethod, r.Diagnostics.PurchasedAreaLowerBoundMethod);
        var best = T.Best(r);
        Assert.Equal(8.4m, best.Metrics.PurchasedAreaM2);
        Assert.True(best.CostBound is { Proven: false, GapM2: 0 });
        Assert.Equal(QualityStatus.BoundedGap, best.QualityStatus);
        Assert.StartsWith("The price equals a lower bound", CutPlanDocument.QualityText(best));
    }

    // ---- §38 physical fit ----------------------------------------------------------------------------------------

    [Fact]
    public void Exact_width_fits_and_one_unit_more_is_rejected_naming_the_binding_width()
    {
        var fits = T.RunOk(T.Job([T.Piece("Full", 2800, 500, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)]));
        var placed = T.Best(fits).Placements.Single();
        Assert.Equal(2800m, placed.Footprint.Width);

        var over = T.Run(T.Job([T.Piece("Full", 2800.1m, 500, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)]));
        Assert.Equal(RunStatus.InvalidInput, over.Status);
        var e = Assert.Single(over.InputErrors);
        Assert.Equal("PIECE_FITS_NO_SOURCE", e.Code);
        Assert.Contains("2,800", e.Message.Replace("2800", "2,800"));
        Assert.Contains("cannot be turned", e.Message);
    }

    // ---- §30, §95: coordinate transformations and their inverses ---------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public void Sheet_transforms_round_trip(int rotation)
    {
        var rng = new Random(rotation + 7);
        for (int i = 0; i < 200; i++)
        {
            decimal w = rng.Next(100, 5000), l = rng.Next(100, 30000);
            decimal x = rng.Next(0, (int)w - 10), y = rng.Next(0, (int)l - 10);
            var r = new RectMm(x, y, rng.Next(1, (int)(w - x)), rng.Next(1, (int)(l - y)));
            var d = SheetTransform.ToDisplay(r, w, l, rotation);
            Assert.Equal(r, SheetTransform.ToCanonical(d, w, l, rotation));
            var (across, down) = SheetTransform.DisplaySize(w, l, rotation);
            Assert.True(d.X >= 0 && d.Y >= 0 && d.X + d.Width <= across && d.Y + d.Length <= down, "a rectangle inside the sheet stays inside the drawn sheet");
            Assert.Equal(r.Width * r.Length, d.Width * d.Length);

            var (dx, dy) = SheetTransform.ToDisplay(x, y, w, l, rotation);
            Assert.Equal((x, y), SheetTransform.ToCanonical(dx, dy, w, l, rotation));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => SheetTransform.ToDisplay(0, 0, 10, 10, 180));
    }

    [Fact]
    public void Padding_footprint_and_pattern_transforms_agree_with_the_solver_and_round_trip()
    {
        var pad = new Padding { Top = 1, Right = 2, Bottom = 3, Left = 4 };
        foreach (bool rotated in new[] { false, true })
        {
            Assert.Equal(pad, SheetTransform.PaddingOnPanel(SheetTransform.PaddingOnSource(pad, rotated), rotated));
            foreach (var axis in new[] { PatternAxis.Length, PatternAxis.Width })
                Assert.Equal(OrientationRules.FinishedDirection(axis, rotated), SheetTransform.FinishedPattern(axis, rotated));
        }

        // Quarter turn clockwise: source-top = panel-left, source-right = panel-top, source-bottom = panel-right, source-left = panel-bottom.
        Assert.Equal(new Padding { Top = 4, Right = 1, Bottom = 2, Left = 3 }, SheetTransform.PaddingOnSource(pad, true));
        Assert.Equal((710m, 460m), SheetTransform.FootprintOnSource(460, 710, true));

        // Every placement the optimiser reports obeys the same rules.
        var (_, _, plan) = MixedPlan();
        foreach (var p in plan.Placements)
        {
            var (across, along) = SheetTransform.FootprintOnSource(p.CutWidthMm, p.CutLengthMm, p.PieceRotated);
            Assert.Equal((p.Footprint.Width, p.Footprint.Length), (across, along));
            var onSource = SheetTransform.PaddingOnSource(p.PaddingMm, p.PieceRotated);
            Assert.Equal(p.Footprint.X + onSource.Left, p.NominalRect.X);
            Assert.Equal(p.Footprint.Y + onSource.Top, p.NominalRect.Y);
        }
    }

    // ---- search internals that must never cost correctness --------------------------------------------------------

    [Fact]
    public void Leftover_resplit_leaves_less_scrap_and_keeps_pieces_in_place()
    {
        // 1,000 × 1,500 sheet with an 850 × 1,000 piece in the corner, cut along first: a 150 × 1,500 strip (scrap) and
        // an 850 × 500 leftover. Cut across first instead: 150 × 1,000 (scrap) and 1,000 × 500, so less scrap.
        var job = T.Job([T.Piece("P", 850, 1000)], [T.Roll("r", 1000)]);
        var p = Normalizer.Normalize(job);
        var root = new Node(new Rect(0, 0, 10000, 15000), null);
        root.Split(CutAxis.Vertical, 8500);
        root.A!.Split(CutAxis.Horizontal, 10000);
        var block = root.A.A!;
        block.Piece = 0;

        Finalizer.ResplitResiduals(root, p);

        Assert.Equal(CutAxis.Horizontal, root.Axis);
        Assert.Equal(10000, root.Pos);
        Assert.Same(block, root.A!.A);
        Assert.Equal(new Rect(0, 0, 8500, 10000), block.R);
        Assert.Equal(0, block.Piece);
        Assert.Equal(new Rect(8500, 0, 1500, 10000), root.A.B!.R);
        Assert.Equal(new Rect(0, 10000, 10000, 5000), root.B!.R);
        Assert.Equal(2, Node.PreOrder(root).Count(n => n.A is not null));
    }

    [Fact]
    public void Final_selection_drops_a_plan_the_checker_rejects()
    {
        var job = T.Job([T.Piece("P", 600, 900, q: 3, pad: 2), T.Piece("Q", 450, 700, q: 2)], [T.Roll("a", 2800), T.Roll("b", 1600, price: 9m)]);
        var p = Normalizer.Normalize(job);
        var options = T.Det();
        var engine = new SearchEngine(p, options, new SearchBudget(10_000, 300, ExecutionMode.Deterministic, default));
        engine.Run();
        var top = engine.Pool.Best!;
        var chosen = engine.Pool.SelectFinal(3, e => e != top && engine.Materialize(e));
        Assert.NotEmpty(chosen);
        Assert.DoesNotContain(chosen, c => c.Entry == top);
        Assert.All(chosen, c => Assert.True(c.Entry.Plan!.Validation.Passed));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(19)]
    public void Every_returned_plan_is_validated_without_per_layout_validation(int seed)
    {
        var job = Fixtures.Random(new Random(seed), instances: 4 + seed % 30);
        var r = T.Run(job, T.Det() with { ValidateEveryKeptPlan = false });
        if (r.Status != RunStatus.Succeeded) return;
        T.AllValid(r, job);
        Assert.All(r.Candidates, c => Assert.True(c.Validation.Passed));
        Assert.True(r.Diagnostics.PlansValidated >= r.Candidates.Count);
    }

    // ---- §36, §47, §104: the printed cut plan ---------------------------------------------------------------------

    [Fact]
    public void Printed_cut_plan_has_one_section_per_source_and_every_cut_step()
    {
        var (job, r, plan) = MixedPlan();
        string html = CutPlanDocument.Render(plan, job, r, Now);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Equal(plan.Sources.Count, System.Text.RegularExpressions.Regex.Count(html, "<section class=\"source\""));
        Assert.Equal(plan.CutSteps.Count, System.Text.RegularExpressions.Regex.Count(html, "<tr data-cut=\""));
        Assert.All(plan.Placements, p => Assert.Contains($"<tr data-piece=\"{p.PieceInstanceId}\">", html));
        Assert.Contains("not actual size", html);
        Assert.Contains("printed 2026-11-17 09:00 UTC", html);
        Assert.Contains(plan.CandidateId, html);
        foreach (var s in plan.Sources.Where(s => s.Kind == SourceKind.WarehouseRemnant))
            Assert.Contains($"<th scope=\"row\">{s.StockId}</th>", html);

        // Each drawing carries a text description and its own pattern ids (several drawings share one page).
        foreach (var (_, svg) in CutPlanSvgRenderer.Render(plan, true))
        {
            Assert.Contains("role=\"img\"", svg);
            Assert.Contains("<title>", svg);
            Assert.DoesNotContain("id=\"hatch\"", svg);
        }

        var repriced = job with { RollOffers = [job.RollOffers[0] with { PricePerSquareMetre = 99m }] };
        Assert.Throws<InvalidOperationException>(() => CutPlanDocument.Render(plan, repriced));
    }

    [Fact]
    public void Warehouse_only_plan_says_no_new_fabric_to_order()
    {
        var job = T.Job([T.Piece("P", 500, 400, q: 2)], [], [T.Stock("R1", 1200, 1000)]);
        var best = T.Best(T.RunOk(job));
        string html = CutPlanDocument.Render(best, job);
        Assert.Contains("No new fabric to order. The selected warehouse pieces cover this job.", html);
        Assert.DoesNotContain(" · printed 20", html); // no print time given, so none is invented
    }
}

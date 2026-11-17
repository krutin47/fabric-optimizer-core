using System.Globalization;
using System.Net;
using System.Text;
using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Printing;

/// <summary>
/// The printable cut plan (handbook §36, §47, §102, §104): one self-contained HTML document with what to order, which
/// warehouse pieces to take, the material ledger, and then one section per physical source with its drawing and the
/// same information as tables (numbered cut steps, pieces with positions, leftovers). The tables are the cut plan; the
/// drawing illustrates them. It prints one source per page with repeated table headers and stays readable in black
/// and white.
/// <para>
/// It is generated only from an independently validated plan and its snapshot, never recomputed, so a printout cannot
/// differ from the plan that was checked.
/// </para>
/// </summary>
public static class CutPlanDocument
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Mm(decimal v) => v.ToString("#,0.###", Inv);
    private static string M2(decimal v) => v.ToString("#,0.######", Inv) + " m²";
    private static string Money(decimal v, string? currency) => v.ToString("#,0.00", Inv) + (string.IsNullOrWhiteSpace(currency) ? "" : " " + currency);

    /// <param name="plan">A plan that passed the independent validator.</param>
    /// <param name="snapshot">The snapshot the plan was computed from (its hash must match).</param>
    /// <param name="result">The run, for its versions; optional.</param>
    /// <param name="printedAtUtc">Print time shown on the document; left out when null.</param>
    public static string Render(CandidateCutPlan plan, OptimizationSnapshot snapshot, OptimizationResult? result = null, DateTimeOffset? printedAtUtc = null)
    {
        if (!plan.Validation.Passed)
            throw new InvalidOperationException("Only independently validated plans can be printed.");
        if (!string.IsNullOrEmpty(plan.SnapshotHash) && plan.SnapshotHash != Infrastructure.OptimizerJson.SnapshotHash(snapshot))
            throw new InvalidOperationException("The plan was computed from a different snapshot; print it from its own snapshot.");

        var context = PrintContext.From(snapshot, result, printedAtUtc);
        var drawings = CutPlanSvgRenderer.Render(plan, snapshot.MaterialDirectional, context).ToDictionary(d => d.SourceId, d => d.Svg);
        var offer = snapshot.RollOffers?.FirstOrDefault(o => o.OfferId == plan.Purchase?.RollOfferId);
        string? overview = CutPlanSvgRenderer.RenderSupplierRollOverview(plan, offer, context.SolverVersion);
        string job = context.Describe();
        string jobName = (string.IsNullOrWhiteSpace(snapshot.JobId), string.IsNullOrWhiteSpace(snapshot.JobRevisionId)) switch
        {
            (false, false) => $" · job {snapshot.JobId} revision {snapshot.JobRevisionId}",
            (false, true) => $" · job {snapshot.JobId}",
            (true, false) => $" · job revision {snapshot.JobRevisionId}",
            _ => "",
        };
        string title = $"Cut plan{jobName}{(plan.Rank > 0 ? $" · plan #{plan.Rank}" : "")}";

        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>{H(title)}</title>\n<style>{Css}</style>\n</head>\n<body>\n");
        sb.Append($"<header>\n<h1>{H(title)}</h1>\n");
        if (job.Length > 0) sb.Append($"<p class=\"meta\">{H(job)}</p>\n");
        sb.Append($"<p class=\"quality\">{H(QualityText(plan))}</p>\n</header>\n<main>\n");

        // ---- what to order ----------------------------------------------------------------------------------------
        sb.Append("<section aria-labelledby=\"order-h\">\n<h2 id=\"order-h\">What to order</h2>\n");
        if (plan.Purchase is not { } p)
        {
            sb.Append("<p class=\"big\">No new fabric to order. The selected warehouse pieces cover this job.</p>\n");
        }
        else
        {
            sb.Append("<table>\n<thead><tr><th scope=\"col\">Roll</th><th scope=\"col\">Height (fixed width of the roll)</th><th scope=\"col\">Order</th><th scope=\"col\">Price per m²</th><th scope=\"col\">Cost</th></tr></thead>\n<tbody><tr>");
            sb.Append($"<td>{H(p.RollOfferId)}</td><td>{Mm(p.OrderedWidthMm)} mm</td>");
            sb.Append(p.FixedLength
                ? $"<td><strong>{Mm(p.OrderedLengthMetres * 1000)} mm</strong> fixed-length stock (test mode)</td>"
                : $"<td><strong>{Mm(p.OrderedLengthMetres)} m</strong> (whole metres)</td>");
            sb.Append($"<td>{Money(p.PricePerSquareMetre, p.Currency)}</td><td>{Money(p.PurchaseCostRounded, p.Currency)} <span class=\"note\">({H(p.CostRounding)})</span></td></tr></tbody>\n</table>\n");
            var surplus = plan.PredictedResiduals.FirstOrDefault(r => r.RoundingSurplus);
            sb.Append($"<p>{H(CutPlanSvgRenderer.OrderDetail(p, surplus))}</p>\n");
            if (overview is not null)
                sb.Append($"<figure class=\"drawing\">{overview}<figcaption>The supplier's whole roll: the ordered part, where the supplier cuts, and what stays with the supplier.</figcaption></figure>\n");
        }

        sb.Append("</section>\n");

        // ---- warehouse pieces -------------------------------------------------------------------------------------
        var stock = plan.Sources.Where(s => s.Kind == SourceKind.WarehouseRemnant).ToList();
        sb.Append("<section aria-labelledby=\"stock-h\">\n<h2 id=\"stock-h\">Warehouse pieces to take</h2>\n");
        if (stock.Count == 0)
        {
            sb.Append("<p>None. No warehouse piece is cut.</p>\n");
        }
        else
        {
            sb.Append("<table>\n<thead><tr><th scope=\"col\">Piece</th><th scope=\"col\">Version</th><th scope=\"col\">Bin</th><th scope=\"col\">Length × height</th><th scope=\"col\">Keepable leftover after cutting</th></tr></thead>\n<tbody>\n");
            foreach (var s in stock)
            {
                decimal kept = plan.PredictedResiduals.Where(r => r.SourceId == s.SourceId && r.Eligible).Sum(r => r.AreaM2);
                sb.Append($"<tr><th scope=\"row\">{H(s.StockId)}</th><td>{s.StockVersion}</td><td>{H(s.BinReference ?? "–")}</td><td>{Mm(s.LengthMm)} × {Mm(s.WidthMm)} mm</td><td>{(kept > 0 ? M2(kept) : "none")}</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n<p class=\"note\">Reserve these pieces before cutting. If one is no longer available, re-run the optimisation; do not swap in another piece.</p>\n");
        }

        sb.Append("</section>\n");

        // ---- material ledger --------------------------------------------------------------------------------------
        var m = plan.Metrics;
        sb.Append("<section aria-labelledby=\"ledger-h\">\n<h2 id=\"ledger-h\">Material summary</h2>\n<table class=\"ledger\">\n<thead><tr><th scope=\"col\">Figure</th><th scope=\"col\" class=\"num\">Value</th></tr></thead>\n<tbody>\n");
        void Row(string k, string v) => sb.Append($"<tr><th scope=\"row\">{H(k)}</th><td class=\"num\">{v}</td></tr>\n");
        Row("Pieces to cut (with padding)", M2(m.RequiredCutAreaM2));
        Row("New fabric ordered", M2(m.PurchasedAreaM2));
        Row("of which the layout uses", M2(m.NewConsumedAreaM2));
        Row("of which is left over from rounding up to whole metres", M2(m.RoundingSurplusAreaM2));
        Row("Warehouse pieces cut into (whole pieces)", M2(m.ExistingStockAreaConsumedM2));
        Row("Warehouse stock left untouched", M2(m.ExistingStockUntouchedAreaM2));
        Row("Leftovers big enough to keep", $"{M2(m.ReusableResidualAreaM2)} ({m.ReusableOffcutCount})");
        Row("Largest keepable leftover", M2(m.LargestReusableAreaM2));
        Row("Scrap (too small to keep)", M2(m.UnusableScrapAreaM2));
        Row("Purchase cost", Money(m.PurchaseCost, m.Currency));
        Row("Cuts", m.TotalCuts.ToString(Inv));
        Row("Sources cut", m.SourcesUsed.ToString(Inv));
        sb.Append("</tbody>\n</table>\n<p class=\"note\">Leftovers are predictions. They become warehouse stock only after they are cut, measured and confirmed.</p>\n</section>\n");

        // ---- one section per physical source ----------------------------------------------------------------------
        for (int i = 0; i < plan.Sources.Count; i++)
        {
            var src = plan.Sources[i];
            string id = $"src-{i + 1}";
            string name = src.Kind == SourceKind.NewRoll
                ? $"New roll {src.RollOfferId}"
                : $"Warehouse piece {src.StockId} (version {src.StockVersion}{(src.BinReference is null ? "" : $", bin {src.BinReference}")})";
            sb.Append($"<section class=\"source\" aria-labelledby=\"{id}\">\n<h2 id=\"{id}\">Sheet {i + 1} of {plan.Sources.Count}: {H(name)}</h2>\n");
            string pattern = CutPlanSvgRenderer.PatternLegend(src, snapshot.MaterialDirectional);
            sb.Append($"<p>{Mm(src.LengthMm)} mm long × {Mm(src.WidthMm)} mm high, drawn with its length left to right from the start of the roll.{(pattern.Length > 0 ? $" The {H(pattern)}." : "")} All positions below are in mm from the sheet's top-left corner as drawn.</p>\n");
            if (drawings.TryGetValue(src.SourceId, out var svg))
                sb.Append($"<figure class=\"drawing\">{svg}<figcaption>Drawing of sheet {i + 1}. Proportional, not actual size: cut by the sizes in the tables.</figcaption></figure>\n");

            CutSteps(sb, plan, src);
            Pieces(sb, plan, src);
            Leftovers(sb, plan, src);
            sb.Append("</section>\n");
        }

        sb.Append("</main>\n<footer>\n");
        sb.Append($"<p>{H(CutPlanSvgRenderer.Footer(plan, context.SolverVersion))}{(string.IsNullOrEmpty(plan.SnapshotHash) ? "" : $" · snapshot {H(plan.SnapshotHash[..12])}")}</p>\n");
        sb.Append("<p>Generated from the checked plan and its frozen job snapshot. Do not edit by hand: re-run the optimisation instead.</p>\n</footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>The honest quality statement for the plan (handbook §32, §45): never "optimal" without a proof.</summary>
    public static string QualityText(CandidateCutPlan plan)
    {
        if (plan.CostBound is { Proven: true, GapM2: 0 })
            return "The price is proven to be the lowest possible for this job (exhaustive search). Scrap, leftovers and cuts are the best found, not proven.";
        if (plan.CostBound is { GapM2: 0 })
            return "The price equals a lower bound, so no cheaper plan exists. Scrap, leftovers and cuts are the best found, not proven.";
        if (plan.Bound is { Proven: true, GapM2: 0 })
            return "The fabric ordered is proven to be the least possible for this job (exhaustive search). Everything else is the best found, not proven.";
        return "Best verified plan found within the search budget. It has passed the independent check; it is not proven to be the best possible.";
    }

    private static void CutSteps(StringBuilder sb, CandidateCutPlan plan, PlanSource src)
    {
        var steps = plan.CutSteps.Where(c => c.SourceId == src.SourceId).ToList();
        sb.Append("<h3>Cut steps</h3>\n");
        if (steps.Count == 0)
        {
            sb.Append("<p>No cuts: the sheet is used whole.</p>\n");
            return;
        }

        sb.Append("<table>\n<thead><tr><th scope=\"col\">Step</th><th scope=\"col\">Direction</th><th scope=\"col\">Position</th><th scope=\"col\">Runs from – to</th><th scope=\"col\" class=\"num\">Length</th></tr></thead>\n<tbody>\n");
        foreach (var s in steps)
        {
            var (fx, fy) = SheetTransform.ToDisplay(s.FromXMm, s.FromYMm, src.WidthMm, src.LengthMm, src.DisplayRotationDegrees);
            var (tx, ty) = SheetTransform.ToDisplay(s.ToXMm, s.ToYMm, src.WidthMm, src.LengthMm, src.DisplayRotationDegrees);
            bool down = fx == tx;
            string direction = down ? "Top to bottom" : "Left to right";
            string position = down ? $"{Mm(fx)} from the left" : $"{Mm(fy)} from the top";
            string span = down ? $"{Mm(Math.Min(fy, ty))} – {Mm(Math.Max(fy, ty))} from the top" : $"{Mm(Math.Min(fx, tx))} – {Mm(Math.Max(fx, tx))} from the left";
            decimal length = down ? Math.Abs(ty - fy) : Math.Abs(tx - fx);
            sb.Append($"<tr data-cut=\"{s.Sequence}\"><th scope=\"row\">{s.Sequence}</th><td>{direction}</td><td>{position}</td><td>{span}</td><td class=\"num\">{Mm(length)}</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n<p class=\"note\">Make the cuts in this order. Each cut runs from edge to edge of the piece it divides.</p>\n");
    }

    private static void Pieces(StringBuilder sb, CandidateCutPlan plan, PlanSource src)
    {
        var pieces = plan.Placements.Where(p => p.SourceId == src.SourceId).ToList();
        sb.Append("<h3>Pieces</h3>\n<table>\n<thead><tr><th scope=\"col\">Piece</th><th scope=\"col\">Label</th><th scope=\"col\">Cut-out W × H</th><th scope=\"col\">Finished W × H</th><th scope=\"col\">Padding T / R / B / L</th><th scope=\"col\">Top-left corner</th><th scope=\"col\">Lies</th><th scope=\"col\">Pattern on the panel</th></tr></thead>\n<tbody>\n");
        bool turnedSheet = src.DisplayRotationDegrees is 90 or 270;
        foreach (var p in pieces)
        {
            var d = SheetTransform.ToDisplay(p.Footprint, src.WidthMm, src.LengthMm, src.DisplayRotationDegrees);
            var pad = p.PaddingMm ?? new Padding();
            bool padded = pad.Top + pad.Right + pad.Bottom + pad.Left > 0;
            // "Turned" as the cutter sees it: the panel's height lies left to right on the drawing.
            bool upright = turnedSheet ? p.PieceRotated : !p.PieceRotated;
            string pattern = p.FinishedPatternDirection switch
            {
                PatternOrientation.Vertical => "↕ runs up the panel",
                PatternOrientation.Horizontal => "↔ runs across the panel",
                _ => "–",
            };
            sb.Append($"<tr data-piece=\"{H(p.PieceInstanceId)}\"><th scope=\"row\">{H(p.PieceInstanceId)}</th><td>{H(p.Label)}</td>");
            sb.Append($"<td><strong>{Mm(p.CutWidthMm)} × {Mm(p.CutLengthMm)}</strong></td><td>{Mm(p.NominalWidthMm)} × {Mm(p.NominalLengthMm)}</td>");
            sb.Append($"<td>{(padded ? $"{Mm(pad.Top)} / {Mm(pad.Right)} / {Mm(pad.Bottom)} / {Mm(pad.Left)}" : "none")}</td>");
            sb.Append($"<td>{Mm(d.X)} from left, {Mm(d.Y)} from top</td><td>{(upright ? "upright" : "turned 90°")}</td><td>{pattern}</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n");
    }

    private static void Leftovers(StringBuilder sb, CandidateCutPlan plan, PlanSource src)
    {
        var rest = plan.PredictedResiduals.Where(r => r.SourceId == src.SourceId).ToList();
        sb.Append("<h3>Leftovers</h3>\n");
        if (rest.Count == 0)
        {
            sb.Append("<p>None: the pieces use the whole sheet.</p>\n");
            return;
        }

        sb.Append("<table>\n<thead><tr><th scope=\"col\">Leftover</th><th scope=\"col\">Size as drawn</th><th scope=\"col\">Top-left corner</th><th scope=\"col\" class=\"num\">Area</th><th scope=\"col\">Keep or scrap</th></tr></thead>\n<tbody>\n");
        foreach (var r in rest)
        {
            var d = SheetTransform.ToDisplay(r.Region, src.WidthMm, src.LengthMm, src.DisplayRotationDegrees);
            string kind = (r.Eligible ? "Keep" : "Scrap") + (r.RoundingSurplus ? " (left over from rounding up the order)" : "");
            sb.Append($"<tr><th scope=\"row\">{H(r.ResidualId)}</th><td>{Mm(d.Width)} × {Mm(d.Length)}</td><td>{Mm(d.X)} from left, {Mm(d.Y)} from top</td><td class=\"num\">{M2(r.AreaM2)}</td><td>{H(kind)}: {H(r.Reason)}</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n");
    }

    private const string Css = """
        :root { color-scheme: light; }
        body { font: 15px/1.45 Helvetica, Arial, sans-serif; color: #111; margin: 24px auto; max-width: 1100px; padding: 0 16px; background: #fff; }
        h1 { font-size: 24px; margin: 0 0 4px; }
        h2 { font-size: 19px; margin: 28px 0 8px; border-bottom: 2px solid #111; padding-bottom: 3px; }
        h3 { font-size: 16px; margin: 18px 0 6px; }
        .meta, .note { color: #333; }
        .note { font-size: 13px; }
        .quality { border-left: 4px solid #111; padding: 4px 10px; background: #f3f3f3; }
        .big { font-size: 18px; font-weight: bold; }
        table { border-collapse: collapse; width: 100%; margin: 6px 0 10px; font-size: 14px; }
        th, td { border: 1px solid #555; padding: 4px 6px; text-align: left; vertical-align: top; }
        thead th { background: #e8e8e8; }
        tbody th { font-weight: bold; }
        .num { text-align: right; font-variant-numeric: tabular-nums; }
        table.ledger { width: auto; min-width: 60%; }
        figure.drawing { margin: 10px 0; }
        figure.drawing svg { max-width: 100%; height: auto; border: 1px solid #ccc; }
        figcaption { font-size: 13px; color: #333; margin-top: 4px; }
        footer { margin-top: 32px; font-size: 12px; color: #333; border-top: 1px solid #999; padding-top: 6px; }
        @page { margin: 12mm; }
        @media print {
          body { margin: 0; max-width: none; font-size: 12px; }
          section.source { break-before: page; }
          thead { display: table-header-group; }
          tr, figure { break-inside: avoid; }
          .quality { background: none; }
          thead th { background: none; border-bottom: 2px solid #000; }
        }
        """;
}

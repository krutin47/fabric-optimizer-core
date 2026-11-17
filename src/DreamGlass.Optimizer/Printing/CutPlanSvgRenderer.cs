using System.Globalization;
using System.Security;
using System.Text;
using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Printing;

/// <summary>
/// What a printout says about where it comes from (handbook §36, §102): the job revision, the material, when it was
/// printed and which solver produced the plan. Everything is optional; a printout never invents a value.
/// </summary>
public sealed record PrintContext
{
    public string? MaterialId { get; init; }
    public string? MaterialName { get; init; }
    public string? JobId { get; init; }
    public string? JobRevisionId { get; init; }

    /// <summary>When the document was produced. Left out of the printout when null (keeps test output stable).</summary>
    public DateTimeOffset? PrintedAtUtc { get; init; }

    /// <summary>The run's solver version; defaults to this library's <see cref="HybridCutOptimizer.SolverVersion"/>.</summary>
    public string? SolverVersion { get; init; }

    public static PrintContext From(OptimizationSnapshot snapshot, OptimizationResult? result = null, DateTimeOffset? printedAtUtc = null) => new()
    {
        MaterialId = snapshot.MaterialId,
        JobId = snapshot.JobId,
        JobRevisionId = snapshot.JobRevisionId,
        PrintedAtUtc = printedAtUtc,
        SolverVersion = result?.SolverVersion,
    };

    /// <summary>"Job J-1 revision 3 · material fabric-linen · printed 2026-11-17 10:00 UTC" (only the parts that are known).</summary>
    public string Describe()
    {
        var parts = new List<string>();
        string job = (string.IsNullOrWhiteSpace(JobId), string.IsNullOrWhiteSpace(JobRevisionId)) switch
        {
            (false, false) => $"Job {JobId} revision {JobRevisionId}",
            (false, true) => $"Job {JobId}",
            (true, false) => $"Job revision {JobRevisionId}",
            _ => "",
        };
        if (job.Length > 0) parts.Add(job);
        string material = string.IsNullOrWhiteSpace(MaterialName) ? MaterialId ?? "" : string.IsNullOrWhiteSpace(MaterialId) ? MaterialName : $"{MaterialName} ({MaterialId})";
        if (material.Length > 0) parts.Add($"material {material}");
        if (PrintedAtUtc is { } at) parts.Add($"printed {at.ToUniversalTime():yyyy-MM-dd HH:mm} UTC");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Workshop cut printouts (spec §8): one SVG per physical source, drawn in the source's display orientation,
/// with numbered edge-to-edge cuts, piece labels (cut-out size incl. padding and finished size), edge dimensions
/// as laid on the table, pattern stripes, residual eligibility and — for a new roll — the order quantity with
/// needed/ordered length dimension lines. Refuses plans that have not passed independent validation.
/// <para>
/// A drawing alone is not a cut plan (handbook §36, §47): <see cref="CutPlanDocument"/> puts each drawing next to
/// the same information as tables. Each SVG carries a text description, a scale bar with the statement that it is
/// proportional rather than actual size, and the pattern direction in words, so it stays usable in black and white.
/// </para>
/// </summary>
public static class CutPlanSvgRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Ink = "#1f4e79";

    /// <param name="plan">A plan that passed the independent validator.</param>
    /// <param name="directionalMaterial">Whether the material has a pattern direction (grain labels are drawn).</param>
    /// <param name="solverVersion">The run's <c>solverVersion</c>, printed in the footer; defaults to this library's
    /// <see cref="HybridCutOptimizer.SolverVersion"/>.</param>
    public static IReadOnlyList<(string SourceId, string Svg)> Render(CandidateCutPlan plan, bool directionalMaterial, string? solverVersion = null) =>
        Render(plan, directionalMaterial, new PrintContext { SolverVersion = solverVersion });

    /// <summary>One SVG per physical source, with the job, material and print date of <paramref name="context"/> in the footer.</summary>
    public static IReadOnlyList<(string SourceId, string Svg)> Render(CandidateCutPlan plan, bool directionalMaterial, PrintContext context)
    {
        if (!plan.Validation.Passed)
            throw new InvalidOperationException("Only independently validated plans can be printed.");
        return plan.Sources.Select(s => (s.SourceId, RenderSource(plan, s, directionalMaterial, context))).ToList();
    }

    /// <summary>Footer line: plan id, the DreamGlass build that printed it, and the solver and checker versions.</summary>
    internal static string Footer(CandidateCutPlan plan, string? solverVersion) =>
        $"Plan {plan.CandidateId} · DreamGlass {HybridCutOptimizer.BuildVersion} · solver {(string.IsNullOrEmpty(solverVersion) ? HybridCutOptimizer.SolverVersion : solverVersion)} · checker {plan.Validation.ValidatorVersion}";

    /// <summary>Short id that is unique per drawing, so several drawings can sit in one page without clashing ids.</summary>
    internal static string DrawingId(CandidateCutPlan plan, string sourceId) =>
        Infrastructure.OptimizerJson.Sha256Hex(plan.CandidateId + "|" + sourceId)[..10];

    /// <summary>Pattern direction on the drawn sheet, in words and with an arrow ("" for plain material).</summary>
    internal static string PatternLegend(PlanSource src, bool directional) => !directional ? ""
        : SheetTransform.PatternRunsAcrossDisplay(src.PatternAxis, src.DisplayRotationDegrees)
            ? "pattern runs ↔ left to right on this sheet"
            : "pattern runs ↕ top to bottom on this sheet";

    /// <summary>A round length whose bar is 60–160 px at this scale, for the scale bar.</summary>
    private static decimal ScaleBarMm(double pxPerMm)
    {
        foreach (decimal mm in new[] { 10m, 20m, 50m, 100m, 200m, 500m, 1000m, 2000m, 5000m, 10000m, 20000m, 50000m })
            if ((double)mm * pxPerMm >= 60) return mm;
        return 100000m;
    }

    private static string N(double v) => v.ToString("0.##", Inv);
    private static string Mm(decimal v) => v.ToString("#,0.###", Inv);
    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    private static string PadText(Padding p) =>
        p.Top == p.Right && p.Right == p.Bottom && p.Bottom == p.Left
            ? Mm(p.Top)
            : $"T{Mm(p.Top)} R{Mm(p.Right)} B{Mm(p.Bottom)} L{Mm(p.Left)}";

    /// <summary>Rough text width for fitting labels (Helvetica-like average glyph width).</summary>
    private static double TextWidth(string s, double fontSize) => s.Length * fontSize * 0.56;

    private static string RenderSource(CandidateCutPlan plan, PlanSource src, bool directional, PrintContext context)
    {
        string? solverVersion = context.SolverVersion;
        double w = (double)src.WidthMm, l = (double)src.LengthMm;
        // 270 (v1.2 default): drawn as unrolled on the table, length left to right from the start of the roll.
        // 90: older results, quarter turn clockwise. 0: canonical frame, length top to bottom.
        bool turned = src.DisplayRotationDegrees is 90 or 270;
        bool ccw = src.DisplayRotationDegrees == 270;
        double dw = turned ? l : w, dh = turned ? w : l;
        // Fit the sheet in a 900 × 900 drawing area so labels stay readable on screen and on paper.
        double scale = Math.Min(900.0 / dw, 900.0 / dh);
        var purchase = src.Kind == SourceKind.NewRoll ? plan.Purchase : null;

        const double left = 30;
        double right = purchase is not null && !turned ? 130 : 30;
        double below = purchase is not null && turned ? 86 : 0;
        double pw = dw * scale, ph = dh * scale;
        double font = Math.Clamp(Math.Min(pw, ph) / 40, 9, 14);
        string job = context.Describe();
        double totalW = Math.Max(pw + left + right, 760);
        // The order sentence wraps onto as many lines as the sheet's width needs, so it is never cut off.
        var detailLines = purchase is null ? []
            : Wrap(OrderDetail(purchase, plan.PredictedResiduals.FirstOrDefault(r => r.RoundingSurplus && r.SourceId == src.SourceId)), 12.5, totalW - 2 * left);
        double top = purchase is not null ? 66 + 16 * Math.Max(0, detailLines.Count - 1) : 46;
        double totalH = top + ph + below + 70 + (job.Length > 0 ? 15 : 0);

        // Canonical → display through the shared transform (counter-clockwise: (x, y) ↦ (y, W − x); clockwise: (x, y) ↦ (L − y, x)).
        (double X, double Y, double W, double H) D(RectMm r)
        {
            var d = SheetTransform.ToDisplay(r, src.WidthMm, src.LengthMm, src.DisplayRotationDegrees);
            return (left + (double)d.X * scale, top + (double)d.Y * scale, (double)d.Width * scale, (double)d.Length * scale);
        }

        (double X, double Y) P(decimal xmm, decimal ymm)
        {
            var (dx, dy) = SheetTransform.ToDisplay(xmm, ymm, src.WidthMm, src.LengthMm, src.DisplayRotationDegrees);
            return (left + (double)dx * scale, top + (double)dy * scale);
        }

        var onSheet = plan.Placements.Where(p => p.SourceId == src.SourceId).ToList();
        int cutCount = plan.CutSteps.Count(c => c.SourceId == src.SourceId);
        string uid = DrawingId(plan, src.SourceId);
        string hatch = $"hatch-{uid}";
        string what = src.Kind == SourceKind.NewRoll ? $"new roll {src.RollOfferId}" : $"warehouse piece {src.StockId}";
        string summary = $"Cutting sheet for {what}, {Mm(src.LengthMm)} mm long and {Mm(src.WidthMm)} mm high: {onSheet.Count} piece{(onSheet.Count == 1 ? "" : "s")} and {cutCount} numbered cut{(cutCount == 1 ? "" : "s")}. "
                         + "The pieces, cuts and leftovers are also listed as tables with this drawing.";
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(totalW)}\" height=\"{N(totalH)}\" viewBox=\"0 0 {N(totalW)} {N(totalH)}\" font-family=\"Helvetica, Arial, sans-serif\" role=\"img\" aria-label=\"{X(summary)}\" data-drawing=\"{uid}\">\n");
        sb.Append($"<title>{X(summary)}</title>\n");
        sb.Append($"<defs><pattern id=\"{hatch}\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"8\" stroke=\"#8a8a8a\" stroke-width=\"2\"/></pattern></defs>\n");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");

        // ---- header: what this sheet is and, for a new roll, exactly how much to order ------------------------------
        if (purchase is not null)
        {
            sb.Append($"<text x=\"{left}\" y=\"26\" font-size=\"17\" font-weight=\"bold\" fill=\"{Ink}\">{X(OrderHeadline(purchase))}</text>\n");
            for (int i = 0; i < detailLines.Count; i++)
                sb.Append($"<text x=\"{left}\" y=\"{48 + 16 * i}\" font-size=\"12.5\">{X(detailLines[i])}</text>\n");
        }
        else
        {
            string bin = src.BinReference is null ? "" : $" · bin {src.BinReference}";
            sb.Append($"<text x=\"{left}\" y=\"26\" font-size=\"17\" font-weight=\"bold\">{X($"FROM WAREHOUSE: {src.StockId} v{src.StockVersion}{bin}")} · {Mm(src.LengthMm)} long × {Mm(src.WidthMm)} mm high</text>\n");
        }

        // ---- sheet and pattern ---------------------------------------------------------------------------------
        var (sx, sy, sw, sh) = D(new RectMm(0, 0, src.WidthMm, src.LengthMm));
        sb.Append($"<rect x=\"{N(sx)}\" y=\"{N(sy)}\" width=\"{N(sw)}\" height=\"{N(sh)}\" fill=\"#f7f3ea\" stroke=\"#222\" stroke-width=\"1.5\"/>\n");
        if (directional)
        {
            bool stripesVertical = (src.PatternAxis == PatternAxis.Length) != turned;
            double step = Math.Max(12, Math.Min(sw, sh) / 30);
            sb.Append("<g stroke=\"#e2d9c4\" stroke-width=\"1\">");
            if (stripesVertical)
                for (double x = sx + step; x < sx + sw; x += step) sb.Append($"<line x1=\"{N(x)}\" y1=\"{N(sy)}\" x2=\"{N(x)}\" y2=\"{N(sy + sh)}\"/>");
            else
                for (double y = sy + step; y < sy + sh; y += step) sb.Append($"<line x1=\"{N(sx)}\" y1=\"{N(y)}\" x2=\"{N(sx + sw)}\" y2=\"{N(y)}\"/>");
            sb.Append("</g>\n");
        }

        // ---- residuals -----------------------------------------------------------------------------------------
        foreach (var r in plan.PredictedResiduals.Where(r => r.SourceId == src.SourceId))
        {
            var (x, y, rw, rh) = D(r.Region);
            string fill = r.Eligible ? "#d8f0d2" : $"url(#{hatch})";
            sb.Append($"<rect data-leftover=\"{X(r.ResidualId)}\" x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(rw)}\" height=\"{N(rh)}\" fill=\"{fill}\" fill-opacity=\"0.85\" stroke=\"#6a6a6a\" stroke-width=\"0.8\"/>\n");
            string kindLabel = r.RoundingSurplus
                ? (r.Eligible ? "reusable surplus" : "surplus (scrap)")
                : (r.Eligible ? "reusable" : "scrap");
            // Size as drawn: left-to-right × top-to-bottom.
            var (across, down) = turned ? (r.Region.Length, r.Region.Width) : (r.Region.Width, r.Region.Length);
            string size = $"{Mm(across)}×{Mm(down)}";
            string label = $"{kindLabel} {size}";
            double fs = font * 0.85, cx = x + rw / 2, cy = y + rh / 2;
            const string labelStyle = "text-anchor=\"middle\" fill=\"#3c5a35\"";
            // One line if it fits; else kind and size on two lines (narrow tall leftovers such as the end of an order);
            // else one line turned to read upwards.
            if (rh > 1.6 * fs && TextWidth(label, fs) < rw - 6)
                sb.Append($"<text x=\"{N(cx)}\" y=\"{N(cy + fs * 0.35)}\" font-size=\"{N(fs)}\" {labelStyle}>{label}</text>\n");
            else if (rh > 3.2 * fs && Math.Max(TextWidth(kindLabel, fs), TextWidth(size, fs)) < rw - 6)
                sb.Append($"<text x=\"{N(cx)}\" y=\"{N(cy - fs * 0.25)}\" font-size=\"{N(fs)}\" {labelStyle}>{kindLabel}<tspan x=\"{N(cx)}\" dy=\"{N(fs * 1.2)}\">{size}</tspan></text>\n");
            else if (rw > 1.6 * fs && TextWidth(label, fs) < rh - 6)
                sb.Append($"<text x=\"{N(cx)}\" y=\"{N(cy + fs * 0.35)}\" font-size=\"{N(fs)}\" {labelStyle} transform=\"rotate(-90 {N(cx)} {N(cy)})\">{label}</text>\n");
        }

        // ---- pieces --------------------------------------------------------------------------------------------
        foreach (var p in onSheet)
        {
            var (x, y, rw, rh) = D(p.Footprint);
            sb.Append($"<rect data-piece=\"{X(p.PieceInstanceId)}\" x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(rw)}\" height=\"{N(rh)}\" fill=\"#cfe2f7\" fill-opacity=\"0.9\" stroke=\"{Ink}\" stroke-width=\"1\"/>\n");
            // The cut-out size is read off the footprint actually cut (panel frame), so the label always matches the
            // drawing even if an older or hand-edited result lacks the cutWidthMm/cutLengthMm fields.
            var pp = p.PaddingMm ?? new Padding();
            bool padded = pp.Top + pp.Right + pp.Bottom + pp.Left > 0;
            decimal cutW = p.PieceRotated ? p.Footprint.Length : p.Footprint.Width;
            decimal cutL = p.PieceRotated ? p.Footprint.Width : p.Footprint.Length;
            if (padded)
            {
                var (nx, ny, nw, nh) = D(p.NominalRect);
                sb.Append($"<rect x=\"{N(nx)}\" y=\"{N(ny)}\" width=\"{N(nw)}\" height=\"{N(nh)}\" fill=\"none\" stroke=\"{Ink}\" stroke-dasharray=\"4 3\" stroke-width=\"0.8\"/>\n");
            }

            // Edge dimensions: the cut-out as laid on the table (top edge = across, left edge = down).
            // Inset past the cut-marker radius so a step number sitting on the edge can never cover the dimension.
            double ef = Math.Max(7, font * 0.72);
            double inset = font * 0.75 + 2;
            bool edges = rw > 4.5 * ef + 2 * inset + 10 && rh > 4 * ef + 2 * inset + 10;
            if (edges)
            {
                decimal acrossMm = turned ? p.Footprint.Length : p.Footprint.Width;
                decimal downMm = turned ? p.Footprint.Width : p.Footprint.Length;
                sb.Append($"<text x=\"{N(x + rw / 2)}\" y=\"{N(y + inset + ef * 0.85)}\" font-size=\"{N(ef)}\" text-anchor=\"middle\" fill=\"{Ink}\">{Mm(acrossMm)}</text>\n");
                double lx = x + inset + ef * 0.6, ly = y + rh / 2;
                sb.Append($"<text x=\"{N(lx)}\" y=\"{N(ly)}\" font-size=\"{N(ef)}\" text-anchor=\"middle\" fill=\"{Ink}\" transform=\"rotate(-90 {N(lx)} {N(ly)})\">{Mm(downMm)}</text>\n");
            }

            string id = p.Label != p.PieceId ? $"{p.PieceInstanceId} {p.Label}" : p.PieceInstanceId;
            string arrow = p.FinishedPatternDirection switch { PatternOrientation.Vertical => "grain ↕", PatternOrientation.Horizontal => "grain ↔", _ => "" };
            // "Turned" as the cutter sees it: the panel's height lies left to right on the drawing.
            bool upright = turned ? p.PieceRotated : !p.PieceRotated;
            string turn = upright ? "" : "turned 90°";
            var lines = new List<(string Text, double Size, bool Bold)> { (id, font, true) };
            if (padded)
            {
                lines.Add(($"Cut-out {Mm(cutW)} × {Mm(cutL)} mm", font * 0.85, true));
                lines.Add(($"finished {Mm(p.NominalWidthMm)} × {Mm(p.NominalLengthMm)} · pad {PadText(pp)}", font * 0.76, false));
            }
            else
            {
                lines.Add(($"{Mm(cutW)} × {Mm(cutL)} mm", font * 0.85, true));
            }

            string extra = string.Join(" · ", new[] { turn, arrow }.Where(t => t.Length > 0));
            if (extra.Length > 0) lines.Add((extra, font * 0.76, false));

            double reserve = edges ? 2 * (inset + ef + 3) : 4;
            double available = rh - reserve;
            var fitted = new List<(string Text, double Size, bool Bold)>();
            double used = 0;
            foreach (var line in lines)
            {
                double size = line.Size;
                while (size > 6 && TextWidth(line.Text, size) > rw - reserve - 4) size -= 0.5;
                if (TextWidth(line.Text, size) > rw - reserve - 4 || used + size * 1.25 > available) break;
                fitted.Add((line.Text, size, line.Bold));
                used += size * 1.25;
            }

            double cy = y + rh / 2 - used / 2;
            foreach (var (text, size, bold) in fitted)
            {
                cy += size * 1.25;
                sb.Append($"<text x=\"{N(x + rw / 2)}\" y=\"{N(cy - size * 0.3)}\" font-size=\"{N(size)}\"{(bold ? " font-weight=\"bold\"" : "")} text-anchor=\"middle\">{X(text)}</text>\n");
            }
        }

        // ---- cuts ----------------------------------------------------------------------------------------------
        foreach (var step in plan.CutSteps.Where(c => c.SourceId == src.SourceId))
        {
            var (x1, y1) = P(step.FromXMm, step.FromYMm);
            var (x2, y2) = P(step.ToXMm, step.ToYMm);
            // Step numbers sit a quarter of the way along the cut so they never cover the centred edge dimensions.
            double along = Math.Abs(x2 - x1) + Math.Abs(y2 - y1);
            double t = along > 10 * font ? 0.25 : 0.5;
            double mx = x1 + (x2 - x1) * t, my = y1 + (y2 - y1) * t;
            sb.Append($"<g data-cut=\"{step.Sequence}\"><line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\" stroke=\"#b02a1d\" stroke-width=\"1.4\"/>");
            sb.Append($"<circle cx=\"{N(mx)}\" cy=\"{N(my)}\" r=\"{N(font * 0.75)}\" fill=\"#b02a1d\"/><text x=\"{N(mx)}\" y=\"{N(my + font * 0.3)}\" font-size=\"{N(font * 0.75)}\" text-anchor=\"middle\" fill=\"#fff\">{step.Sequence}</text></g>\n");
        }

        // ---- order dimension lines (new roll) ------------------------------------------------------------------
        if (purchase is not null)
        {
            // Along the far edge: the bottom edge when the length runs left to right, else the right edge.
            decimal edge = ccw ? 0 : src.WidthMm;
            Dimension(sb, P(edge, 0), P(edge, purchase.ConsumedLengthMm), turned, 22,
                $"needed {Mm(purchase.ConsumedLengthMm)}", "#c0392b");
            Dimension(sb, P(edge, 0), P(edge, src.LengthMm), turned, 62,
                $"ordered {Mm(src.LengthMm)}", Ink);
        }

        double legendY = top + ph + below + 20;
        sb.Append($"<text x=\"{left}\" y=\"{N(legendY)}\" font-size=\"11\" fill=\"#444\">Cut in numbered order, each cut edge to edge (red) · blue = cut-out, dashed = finished size · green = reusable leftover · hatched = scrap · sizes in mm</text>\n");

        // Scale bar and statement (handbook §36, §102): the drawing is proportional, never a ruler.
        decimal barMm = ScaleBarMm(scale);
        double barPx = (double)barMm * scale, barY = legendY + 14;
        sb.Append($"<g stroke=\"#222\" stroke-width=\"1.4\"><line x1=\"{left}\" y1=\"{N(barY)}\" x2=\"{N(left + barPx)}\" y2=\"{N(barY)}\"/><line x1=\"{left}\" y1=\"{N(barY - 4)}\" x2=\"{left}\" y2=\"{N(barY + 4)}\"/><line x1=\"{N(left + barPx)}\" y1=\"{N(barY - 4)}\" x2=\"{N(left + barPx)}\" y2=\"{N(barY + 4)}\"/></g>");
        string pattern = PatternLegend(src, directional);
        string scaleText = $"= {Mm(barMm)} mm · proportional drawing, not actual size: cut by the printed sizes" + (pattern.Length > 0 ? $" · {pattern}" : "");
        sb.Append($"<text x=\"{N(left + barPx + 8)}\" y=\"{N(barY + 4)}\" font-size=\"11\" fill=\"#444\">{X(scaleText)}</text>\n");
        double footY = legendY + 33;
        if (job.Length > 0)
        {
            sb.Append($"<text x=\"{left}\" y=\"{N(footY)}\" font-size=\"10\" fill=\"#444\">{X(job)}</text>\n");
            footY += 15;
        }

        sb.Append($"<text x=\"{left}\" y=\"{N(footY)}\" font-size=\"9\" fill=\"#666\">{X(Footer(plan, solverVersion))}</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Full-roll view for a plan that orders from a supplier: the supplier's whole roll drawn to scale left to right,
    /// with the ordered part (its pieces, leftovers and cuts), a dashed line where the supplier cuts, the rest of the
    /// roll greyed out as staying with the supplier, and a metre ruler. Returns null when the plan orders nothing or
    /// the roll's length is unknown (no <c>maxOrderMetres</c> or <c>fixedLengthMm</c> on the offer).
    /// </summary>
    public static string? RenderSupplierRollOverview(CandidateCutPlan plan, RollOffer? offer, string? solverVersion = null)
    {
        if (!plan.Validation.Passed)
            throw new InvalidOperationException("Only independently validated plans can be printed.");
        var purchase = plan.Purchase;
        var src = plan.Sources.FirstOrDefault(x => x.Kind == SourceKind.NewRoll);
        if (purchase is null || src is null || offer is null || offer.OfferId != purchase.RollOfferId) return null;
        decimal? fullMm = offer.FixedLengthMm ?? (offer.MaxOrderMetres is int max ? max * 1000m : null);
        if (fullMm is not decimal full) return null;
        decimal ordered = src.LengthMm;
        if (full < ordered) full = ordered;

        double w = (double)src.WidthMm, lo = (double)ordered, lf = (double)full;
        const double left = 30, top = 84, drawW = 1200;
        double sx = drawW / lf;
        double sy = Math.Max(sx, 90 / w); // long rolls: keep the height readable (noted in the legend)
        bool stretched = sy > sx * 1.001;
        double ph = w * sy, rollBottom = top + ph;
        double totalW = left + drawW + 40, totalH = rollBottom + 164;

        (double X, double Y, double W, double H) R(RectMm r) =>
            (left + (double)r.Y * sx, top + (w - (double)r.X - (double)r.Width) * sy, (double)r.Length * sx, (double)r.Width * sy);
        (double X, double Y) Pt(decimal x, decimal y) => (left + (double)y * sx, top + (w - (double)x) * sy);
        string M(decimal mm) => Mm(mm / 1000m);

        string uid = DrawingId(plan, "overview:" + src.SourceId);
        string hatch = $"hatch-{uid}", rest = $"rest-{uid}";
        string summary = purchase.FixedLength
            ? $"The whole fixed-length roll {offer.OfferId}, {M(full)} m long, with the pieces drawn on it."
            : $"The supplier's roll {offer.OfferId}, {M(full)} m long: the first {M(ordered)} m are ordered and hold the pieces; the other {M(full - ordered)} m stay with the supplier.";
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(totalW)}\" height=\"{N(totalH)}\" viewBox=\"0 0 {N(totalW)} {N(totalH)}\" font-family=\"Helvetica, Arial, sans-serif\" role=\"img\" aria-label=\"{X(summary)}\" data-drawing=\"{uid}\">\n");
        sb.Append($"<title>{X(summary)}</title>\n");
        sb.Append($"<defs><pattern id=\"{hatch}\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"8\" stroke=\"#8a8a8a\" stroke-width=\"2\"/></pattern>");
        sb.Append($"<pattern id=\"{rest}\" width=\"10\" height=\"10\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(-45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"10\" stroke=\"#d3d7dd\" stroke-width=\"3\"/></pattern></defs>\n");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");

        // ---- header -----------------------------------------------------------------------------------------------
        sb.Append($"<text x=\"{left}\" y=\"26\" font-size=\"17\" font-weight=\"bold\">{X($"SUPPLIER ROLL: {offer.OfferId} · {M(full)} m × {Mm(src.WidthMm)} mm high")}</text>\n");
        string line2 = purchase.FixedLength
            ? "Fixed-length stock (test mode): the whole roll is treated as bought."
            : full == ordered
                ? $"The whole {M(full)} m roll is ordered."
                : $"The supplier cuts {M(ordered)} m from this roll for you. The other {M(full - ordered)} m stay with the supplier and are not bought.";
        sb.Append($"<text x=\"{left}\" y=\"48\" font-size=\"12.5\">{X(line2)}</text>\n");

        // ---- the rest of the roll (not bought) --------------------------------------------------------------------
        if (lf > lo)
        {
            double rx = left + lo * sx, rw = (lf - lo) * sx;
            sb.Append($"<rect x=\"{N(rx)}\" y=\"{N(top)}\" width=\"{N(rw)}\" height=\"{N(ph)}\" fill=\"#eef0f3\"/>");
            sb.Append($"<rect x=\"{N(rx)}\" y=\"{N(top)}\" width=\"{N(rw)}\" height=\"{N(ph)}\" fill=\"url(#{rest})\"/>\n");
            string restLabel = $"stays with the supplier · {M(full - ordered)} m not bought";
            if (TextWidth(restLabel, 12) < rw - 12)
                sb.Append($"<text x=\"{N(rx + rw / 2)}\" y=\"{N(top + ph / 2 + 4)}\" font-size=\"12\" text-anchor=\"middle\" fill=\"#6b7280\">{X(restLabel)}</text>\n");
        }

        // ---- the ordered part: leftovers, pieces and cuts as on the cutting sheet ----------------------------------
        sb.Append($"<rect x=\"{left}\" y=\"{N(top)}\" width=\"{N(lo * sx)}\" height=\"{N(ph)}\" fill=\"#f7f3ea\"/>\n");
        foreach (var r in plan.PredictedResiduals.Where(r => r.SourceId == src.SourceId))
        {
            var (x, y, rw, rh) = R(r.Region);
            sb.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(rw)}\" height=\"{N(rh)}\" fill=\"{(r.Eligible ? "#d8f0d2" : $"url(#{hatch})")}\" stroke=\"#9a9a9a\" stroke-width=\"0.5\"/>\n");
        }

        foreach (var p in plan.Placements.Where(p => p.SourceId == src.SourceId))
        {
            var (x, y, rw, rh) = R(p.Footprint);
            sb.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(rw)}\" height=\"{N(rh)}\" fill=\"#cfe2f7\" stroke=\"{Ink}\" stroke-width=\"0.8\"/>\n");
            if (TextWidth(p.PieceInstanceId, 10) < rw - 4 && rh > 14)
                sb.Append($"<text x=\"{N(x + rw / 2)}\" y=\"{N(y + rh / 2 + 3.5)}\" font-size=\"10\" font-weight=\"bold\" text-anchor=\"middle\">{X(p.PieceInstanceId)}</text>\n");
        }

        foreach (var step in plan.CutSteps.Where(c => c.SourceId == src.SourceId))
        {
            var (x1, y1) = Pt(step.FromXMm, step.FromYMm);
            var (x2, y2) = Pt(step.ToXMm, step.ToYMm);
            sb.Append($"<line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\" stroke=\"#c0392b\" stroke-width=\"1\"/>\n");
        }

        // Whole roll outline, then the supplier's cut at the ordered length.
        sb.Append($"<rect x=\"{left}\" y=\"{N(top)}\" width=\"{N(lf * sx)}\" height=\"{N(ph)}\" fill=\"none\" stroke=\"#222\" stroke-width=\"1.2\"/>\n");
        double cx = left + lo * sx;
        sb.Append($"<line x1=\"{N(cx)}\" y1=\"{N(top - 14)}\" x2=\"{N(cx)}\" y2=\"{N(rollBottom + 6)}\" stroke=\"{Ink}\" stroke-width=\"2.2\" stroke-dasharray=\"7 4\"/>\n");
        string cutLabel = $"✂ supplier cuts here · {M(ordered)} m";
        bool labelLeft = cx + TextWidth(cutLabel, 12) + 8 > left + drawW;
        sb.Append($"<text x=\"{N(labelLeft ? cx - 6 : cx + 6)}\" y=\"{N(top - 6)}\" font-size=\"12\" font-weight=\"bold\" fill=\"{Ink}\" text-anchor=\"{(labelLeft ? "end" : "start")}\">{X(cutLabel)}</text>\n");

        // ---- metre ruler -----------------------------------------------------------------------------------------
        double rulerY = rollBottom + 10;
        double perMetre = 1000 * sx;
        int major = new[] { 1, 2, 5, 10, 20, 50, 100, 200, 500 }.FirstOrDefault(k => k * perMetre >= 55, 1000);
        int metres = (int)Math.Floor(lf / 1000);
        sb.Append($"<line x1=\"{left}\" y1=\"{N(rulerY)}\" x2=\"{N(left + lf * sx)}\" y2=\"{N(rulerY)}\" stroke=\"#555\" stroke-width=\"1\"/>");
        for (int m = 0; m <= metres; m++)
        {
            bool isMajor = m % major == 0;
            if (!isMajor && perMetre < 4) continue;
            double x = left + m * perMetre;
            sb.Append($"<line x1=\"{N(x)}\" y1=\"{N(rulerY)}\" x2=\"{N(x)}\" y2=\"{N(rulerY + (isMajor ? 7 : 4))}\" stroke=\"#555\" stroke-width=\"1\"/>");
            if (isMajor && (left + lf * sx) - x > 48) // keep clear of the end label
                sb.Append($"<text x=\"{N(x)}\" y=\"{N(rulerY + 19)}\" font-size=\"10\" text-anchor=\"middle\" fill=\"#555\">{m} m</text>");
        }

        sb.Append($"<text x=\"{N(left + lf * sx)}\" y=\"{N(rulerY + 19)}\" font-size=\"10\" text-anchor=\"end\" fill=\"#555\">{M(full)} m</text>\n");

        // ---- ordered vs whole roll -------------------------------------------------------------------------------
        void Span(double y, double x2, string label, string color)
        {
            sb.Append($"<g stroke=\"{color}\" stroke-width=\"1.2\"><line x1=\"{left}\" y1=\"{N(y)}\" x2=\"{N(x2)}\" y2=\"{N(y)}\"/>");
            sb.Append($"<line x1=\"{left}\" y1=\"{N(y - 5)}\" x2=\"{left}\" y2=\"{N(y + 5)}\"/><line x1=\"{N(x2)}\" y1=\"{N(y - 5)}\" x2=\"{N(x2)}\" y2=\"{N(y + 5)}\"/></g>");
            bool fits = TextWidth(label, 11) < x2 - left - 8;
            sb.Append($"<text x=\"{N(fits ? (left + x2) / 2 : x2 + 6)}\" y=\"{N(y + 4)}\" font-size=\"11\" font-weight=\"bold\" fill=\"{color}\" text-anchor=\"{(fits ? "middle" : "start")}\"");
            sb.Append(fits ? $" dy=\"12\">" : ">");
            sb.Append($"{X(label)}</text>\n");
        }

        Span(rulerY + 42, cx, $"ordered {M(ordered)} m", Ink);
        Span(rulerY + 78, left + lf * sx, $"supplier roll {M(full)} m", "#6b7280");

        string legend = "Blue = pieces · green = reusable leftover · hatched = scrap · grey = stays with the supplier · dashed line = where the supplier cuts"
            + (stretched ? " · height not to scale" : "");
        sb.Append($"<text x=\"{left}\" y=\"{N(totalH - 28)}\" font-size=\"11\" fill=\"#555\">{X(legend)}</text>\n");
        sb.Append($"<text x=\"{left}\" y=\"{N(totalH - 13)}\" font-size=\"9\" fill=\"#888\">{X(Footer(plan, solverVersion))}</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>"ORDER FROM SUPPLIER: 17 m of roll-2800 (2,800 mm wide)".</summary>
    internal static string OrderHeadline(PurchaseLine p) => p.FixedLength
        ? $"FIXED-LENGTH STOCK (test mode): {Mm(p.OrderedLengthMetres * 1000)} mm of {p.RollOfferId} ({Mm(p.OrderedWidthMm)} mm high)"
        : $"ORDER FROM SUPPLIER: {Mm(p.OrderedLengthMetres)} m of {p.RollOfferId} ({Mm(p.OrderedWidthMm)} mm high)";

    /// <summary>
    /// "Layout needs 16,680 mm (16,500 + 180 padding) → rounded up to whole metres. The 320 mm left over goes to the
    /// warehouse." The leftover only goes to the warehouse when it is big enough to keep (<paramref name="surplus"/>
    /// is eligible); otherwise it is called scrap, with the reason.
    /// </summary>
    internal static string OrderDetail(PurchaseLine p, PredictedResidual? surplus = null)
    {
        string needed = p.PaddingLengthMm > 0
            ? $"{Mm(p.ConsumedLengthMm)} mm ({Mm(p.LengthWithoutPaddingMm)} + {Mm(p.PaddingLengthMm)} padding)"
            : $"{Mm(p.ConsumedLengthMm)} mm";
        string rest = p.RoundingSurplusMm <= 0 ? " Nothing is left over."
            : surplus is { Eligible: false } ? $" The {Mm(p.RoundingSurplusMm)} mm left over is scrap: too small to keep ({LowerFirst(surplus.Reason)})."
            : $" The {Mm(p.RoundingSurplusMm)} mm left over goes to the warehouse.";
        return p.FixedLength
            ? $"Layout needs {needed}; the whole fixed length is used as stock.{rest}"
            : $"Layout needs {needed} → rounded up to whole metres.{rest}";
    }

    /// <summary>Greedy word wrap by the same rough glyph width the labels use.</summary>
    internal static List<string> Wrap(string text, double fontSize, double width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && TextWidth(line + " " + word, fontSize) > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>Dimension line along the sheet's far edge (right edge, or bottom edge for a rotated sheet).</summary>
    private static void Dimension(StringBuilder sb, (double X, double Y) a, (double X, double Y) b, bool turned, double offset, string label, string color)
    {
        if (!turned)
        {
            double x = a.X + offset;
            sb.Append($"<g stroke=\"{color}\" stroke-width=\"1\"><line x1=\"{N(x)}\" y1=\"{N(a.Y)}\" x2=\"{N(x)}\" y2=\"{N(b.Y)}\"/>");
            sb.Append($"<line x1=\"{N(x - 5)}\" y1=\"{N(a.Y)}\" x2=\"{N(x + 5)}\" y2=\"{N(a.Y)}\"/><line x1=\"{N(x - 5)}\" y1=\"{N(b.Y)}\" x2=\"{N(x + 5)}\" y2=\"{N(b.Y)}\"/></g>\n");
            double my = (a.Y + b.Y) / 2, tx = x + 13;
            sb.Append($"<text x=\"{N(tx)}\" y=\"{N(my)}\" font-size=\"11\" font-weight=\"bold\" fill=\"{color}\" text-anchor=\"middle\" transform=\"rotate(-90 {N(tx)} {N(my)})\">{X(label)}</text>\n");
        }
        else
        {
            double y = a.Y + offset;
            sb.Append($"<g stroke=\"{color}\" stroke-width=\"1\"><line x1=\"{N(a.X)}\" y1=\"{N(y)}\" x2=\"{N(b.X)}\" y2=\"{N(y)}\"/>");
            sb.Append($"<line x1=\"{N(a.X)}\" y1=\"{N(y - 5)}\" x2=\"{N(a.X)}\" y2=\"{N(y + 5)}\"/><line x1=\"{N(b.X)}\" y1=\"{N(y - 5)}\" x2=\"{N(b.X)}\" y2=\"{N(y + 5)}\"/></g>\n");
            sb.Append($"<text x=\"{N((a.X + b.X) / 2)}\" y=\"{N(y + 16)}\" font-size=\"11\" font-weight=\"bold\" fill=\"{color}\" text-anchor=\"middle\">{X(label)}</text>\n");
        }
    }
}

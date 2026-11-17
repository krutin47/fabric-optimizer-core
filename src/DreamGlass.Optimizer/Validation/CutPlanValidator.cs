using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;

namespace DreamGlass.Optimizer.Validation;

/// <summary>
/// Independent plan validator (spec §7) — a release blocker. It shares only the public DTO contracts with the
/// generator: unit conversion, orientation semantics, roll-order rules and remnant eligibility are re-implemented
/// here from the written conventions, so a generator bug cannot silently validate itself.
/// <para>Accepted-plan invariant: the plan belongs to exactly this snapshot; every physical source is one the
/// snapshot offers (same organisation, available, not excluded) and is exactly partitioned into requested footprints
/// and residual rectangles by a legal guillotine tree; all metrics and the material ledger are recomputed and match;
/// and the purchase is a minimal permitted order covering the layout.</para>
/// <para>Failure codes are stable identifiers (handbook §31); the list is in <c>docs/testing/validator-codes.md</c>.</para>
/// </summary>
public sealed class CutPlanValidator : ICutPlanValidator
{
    public const string Version = "dreamglass-validator/1.3.0";

    /// <summary>Largest coordinate or dimension accepted, in scaled units, so that every area fits 64-bit arithmetic.</summary>
    private const long MaxUnits = int.MaxValue;
    private const int MaxFailures = 100;
    private const int MaxNodes = 2_000_000;

    public PlanValidationOutcome Validate(OptimizationSnapshot input, CandidateCutPlan candidate)
    {
        var run = new Run(input, candidate);
        try
        {
            run.Execute();
        }
        catch (OverflowException)
        {
            run.Fail("ARITHMETIC_OVERFLOW", "Plan dimensions overflow the validator's exact arithmetic.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // An unreadable plan is a failed plan, never a passed one.
            run.Fail("VALIDATOR_EXCEPTION", $"Plan could not be validated: {ex.GetType().Name}: {ex.Message}");
        }

        return new PlanValidationOutcome { ValidatorVersion = Version, Passed = run.Failures.Count == 0, Failures = run.Failures };
    }

    private sealed record Src(PlanSource Dto, long W, long L, PatternAxis Axis, bool IsRoll, RollOffer? Offer, int DisplayRotation);

    private sealed record Plc(Placement Dto, PieceRequest Piece, long X, long Y, long W, long H);

    private sealed record Res(PredictedResidual Dto, long X, long Y, long W, long H);

    private sealed class Run(OptimizationSnapshot input, CandidateCutPlan plan)
    {
        public readonly List<ValidationFailure> Failures = [];
        private readonly long _scale = input?.Precision?.UnitsPerMm ?? 0;

        public void Fail(string code, string message, string? source = null, string? piece = null, string? node = null)
        {
            if (Failures.Count < MaxFailures) Failures.Add(new ValidationFailure(code, message, source, piece, node));
        }

        // ---- independent numeric helpers -------------------------------------------------------------------
        private bool U(decimal mm, string what, out long units, string? source = null, string? piece = null, string? node = null)
        {
            units = 0;
            decimal scaled;
            try
            {
                scaled = mm * _scale;
            }
            catch (OverflowException)
            {
                Fail("ARITHMETIC_OVERFLOW", $"{what} is out of range.", source, piece, node);
                return false;
            }

            if (scaled > MaxUnits || scaled < -MaxUnits)
            {
                Fail("ARITHMETIC_OVERFLOW", $"{what} = {mm} mm is outside the range the validator can check exactly.", source, piece, node);
                return false;
            }

            if (scaled != decimal.Truncate(scaled))
            {
                Fail("PRECISION", $"{what} = {mm} mm is not representable at 1/{_scale} mm.", source, piece, node);
                return false;
            }

            units = (long)scaled;
            return true;
        }

        /// <summary>Squared units → m². Sums of areas are carried in 128 bits, so no total can wrap around.</summary>
        private decimal Area(Int128 areaUnits) => (decimal)areaUnits / (_scale * _scale * 1_000_000m);

        private static Int128 Total(IEnumerable<long> areas)
        {
            Int128 sum = 0;
            foreach (long a in areas) sum += a;
            return sum;
        }

        private decimal Mm(long units) => (decimal)units / _scale;

        private bool RectU(RectMm? r, string what, out (long X, long Y, long W, long H) u, string? source = null, string? piece = null, string? node = null)
        {
            u = default;
            if (r is null)
            {
                Fail("SCHEMA", $"{what} is missing.", source, piece, node);
                return false;
            }

            bool ok = U(r.X, what + ".x", out long x, source, piece, node)
                      & U(r.Y, what + ".y", out long y, source, piece, node)
                      & U(r.Width, what + ".width", out long w, source, piece, node)
                      & U(r.Length, what + ".length", out long h, source, piece, node);
            u = (x, y, w, h);
            return ok;
        }

        /// <summary>Finished pattern direction: the pattern lies along the panel's length axis exactly when the source
        /// pattern runs along y and the panel is unrotated, or runs along x and the panel is rotated a quarter turn.</summary>
        private static PatternOrientation Finished(PatternAxis sourceAxis, bool rotated)
        {
            bool patternAlongPanelLength = (sourceAxis == PatternAxis.Length) != rotated;
            return patternAlongPanelLength ? PatternOrientation.Vertical : PatternOrientation.Horizontal;
        }

        private (bool Eligible, bool Thin) Eligibility(long w, long h)
        {
            var pol = input.MinimumReusableRemnant;
            if (pol is null) return (false, false);
            long s = Math.Min(w, h), l = Math.Max(w, h);
            long minS = (long)(pol.MinWidthMm * _scale), minL = (long)(pol.MinLengthMm * _scale);
            bool thin = s < minS;
            if (s < minS || l < minL) return (false, thin);
            if (pol.MinAreaM2 is decimal a && Area(w * h) < a) return (false, thin);
            if (pol.MaxAspectRatio is decimal r && (decimal)l / s > r) return (false, thin);
            return (true, false);
        }

        // ---- validation ----------------------------------------------------------------------------------
        public void Execute()
        {
            if (input is null || plan is null)
            {
                Fail("SCHEMA", "Input snapshot and candidate are both required.");
                return;
            }

            if (_scale is not (1 or 10 or 100 or 1000))
            {
                Fail("SCHEMA", "Unsupported precision.unitsPerMm.");
                return;
            }

            if (string.IsNullOrWhiteSpace(plan.CandidateId)) Fail("SCHEMA", "candidateId is missing.");

            // A plan is only valid for the exact snapshot it was computed from (handbook A5: input.version == candidate.inputVersion).
            if (string.IsNullOrEmpty(plan.SnapshotHash) || plan.SnapshotHash != OptimizerJson.SnapshotHash(input))
                Fail("INPUT_VERSION_MISMATCH", "The plan was not computed from this snapshot (the job, its stock or its prices differ). Re-run the optimisation.");
            if ((plan.JobRevisionId ?? "") != (input.JobRevisionId ?? ""))
                Fail("INPUT_VERSION_MISMATCH", $"The plan is for job revision '{plan.JobRevisionId}', the snapshot is revision '{input.JobRevisionId}'.");

            if (plan.QualityStatus == QualityStatus.OptimalProven)
                Fail("UNVERIFIABLE_CLAIM", "OptimalProven requires a proof of the full lexicographic objective, which v1 cannot provide.");
            CheckQualityClaim();
            if (plan.Sources is null || plan.Placements is null || plan.CutTrees is null || plan.CutSteps is null || plan.PredictedResiduals is null || plan.Metrics is null)
            {
                Fail("SCHEMA", "Candidate is missing sources, placements, cut trees, cut steps, residuals or metrics.");
                return;
            }

            var sources = CheckSources();
            var placements = CheckPlacements(sources);
            CheckOverlap(sources, placements);
            var residuals = CollectResiduals(sources);
            var (cutNodes, parentOf, cutsBySource) = CheckTrees(sources, placements, residuals);
            CheckCutSteps(cutNodes, parentOf);
            CheckPurchase(sources, placements, residuals);
            CheckMetrics(sources, placements, residuals, cutsBySource.Values.Sum());
        }

        /// <summary>
        /// A quality label must be backed by the bound it rests on, and the bound's own arithmetic must hold. Whether a
        /// bound is true cannot be re-derived here; what is checked is that the plan claims no more than it shows.
        /// </summary>
        private void CheckQualityClaim()
        {
            void CheckBound(ObjectiveBound? b, string name, decimal value)
            {
                if (b is null) return;
                if (b.GapM2 != value - b.LowerBoundM2) Fail("UNVERIFIABLE_CLAIM", $"{name}: the gap is not the plan's value minus the bound.");
                if (b.GapM2 < 0 && b.Proven) Fail("UNVERIFIABLE_CLAIM", $"{name}: the plan is below a bound that is marked proven.");
                decimal? pct = b.LowerBoundM2 > 0 ? Math.Round(100m * b.GapM2 / b.LowerBoundM2, 2) : null;
                if (b.GapPercentOfBound != pct) Fail("UNVERIFIABLE_CLAIM", $"{name}: the gap percentage must be taken of the bound.");
                if (string.IsNullOrWhiteSpace(b.Scope) || string.IsNullOrWhiteSpace(b.Method)) Fail("UNVERIFIABLE_CLAIM", $"{name}: a bound must state its scope and method.");
            }

            var m = plan.Metrics;
            if (m is null) return;
            CheckBound(plan.Bound, "bound", m.PurchasedAreaM2);
            CheckBound(plan.CostBound, "costBound", m.PurchaseCost);
            if (plan.QualityStatus == QualityStatus.BoundedGap
                && !new[] { plan.Bound, plan.CostBound }.Any(b => b is not null && (b.Proven || b.GapM2 == 0)))
                Fail("UNVERIFIABLE_CLAIM", "BoundedGap needs a proven bound or a bound the plan attains.");
        }

        private Dictionary<string, Src> CheckSources()
        {
            var result = new Dictionary<string, Src>(StringComparer.Ordinal);
            var stockSeen = new HashSet<string>(StringComparer.Ordinal);
            int rolls = 0;
            foreach (var s in plan.Sources)
            {
                if (s is null)
                {
                    Fail("SCHEMA", "Null source entry.");
                    continue;
                }

                if (!U(s.WidthMm, "source width", out long w, s.SourceId) | !U(s.LengthMm, "source length", out long len, s.SourceId)) continue;
                if (w <= 0 || len <= 0) Fail("SOURCE_DIMENSIONS", "Source dimensions must be positive.", s.SourceId);
                RollOffer? offer = null;
                if (s.Kind == SourceKind.WarehouseRemnant)
                {
                    var stock = input.Stock?.FirstOrDefault(x => x is not null && x.StockId == s.StockId);
                    if (stock is null)
                    {
                        Fail("UNKNOWN_SOURCE", $"Remnant '{s.StockId}' is not in the input inventory.", s.SourceId);
                        continue;
                    }

                    if (s.SourceId != stock.StockId) Fail("SOURCE_ID_MISMATCH", "Remnant sourceId must equal its stockId.", s.SourceId);
                    if (!stockSeen.Add(stock.StockId)) Fail("DUPLICATE_SOURCE", $"Remnant '{stock.StockId}' is used more than once.", s.SourceId);
                    if (s.WidthMm != stock.WidthMm || s.LengthMm != stock.LengthMm) Fail("STOCK_MISMATCH", "Remnant dimensions differ from inventory.", s.SourceId);
                    if (s.PatternAxis != stock.PatternAxis) Fail("STOCK_MISMATCH", "Remnant pattern axis differs from inventory.", s.SourceId);
                    if (s.StockVersion != stock.Version) Fail("STOCK_MISMATCH", "Remnant version differs from the snapshot (stale stock).", s.SourceId);
                    if (!stock.Available) Fail("STOCK_UNAVAILABLE", "Remnant is not available.", s.SourceId);
                    if (stock.MaterialId is not null && stock.MaterialId != input.MaterialId) Fail("STOCK_MISMATCH", "Remnant material differs from the job.", s.SourceId);
                    if (!string.Equals(stock.OrganizationId, input.OrganizationId, StringComparison.Ordinal))
                        Fail("ORGANIZATION_MISMATCH", "Remnant does not belong to the organisation that owns the job.", s.SourceId);
                    if (input.ExcludedStock?.Any(x => x is not null && x.StockId == stock.StockId) == true)
                        Fail("EXCLUDED_STOCK_USED", $"Remnant '{stock.StockId}' was excluded from this job.", s.SourceId);
                    if (s.RollOfferId is not null) Fail("SCHEMA", "A remnant cannot reference a roll offer.", s.SourceId);
                }
                else if (s.Kind == SourceKind.NewRoll)
                {
                    rolls++;
                    offer = input.RollOffers?.FirstOrDefault(o => o is not null && o.OfferId == s.RollOfferId);
                    if (offer is null)
                    {
                        Fail("UNKNOWN_OFFER", $"Roll offer '{s.RollOfferId}' is not in the input.", s.SourceId);
                        continue;
                    }

                    if (s.SourceId != "new-roll:" + offer.OfferId) Fail("SOURCE_ID_MISMATCH", "New-roll sourceId must be 'new-roll:{offerId}'.", s.SourceId);
                    if (s.WidthMm != offer.WidthMm) Fail("PURCHASE_INVALID", "Roll width differs from the offer.", s.SourceId);
                    if (s.PatternAxis != offer.PatternAxis) Fail("PURCHASE_INVALID", "Roll pattern axis differs from the offer.", s.SourceId);
                    if (s.StockId is not null) Fail("SCHEMA", "A new roll cannot reference stock.", s.SourceId);
                }
                else
                {
                    Fail("SCHEMA", "Unknown source kind.", s.SourceId);
                    continue;
                }

                if (s.DisplayRotationDegrees is not (0 or 90 or 270)) Fail("TRANSFORM_INVALID", "Display rotation must be 0, 90 or 270 degrees.", s.SourceId);
                bool turned = s.DisplayRotationDegrees is 90 or 270;
                if (s.DisplayWidthMm != (turned ? s.LengthMm : s.WidthMm) || s.DisplayLengthMm != (turned ? s.WidthMm : s.LengthMm))
                    Fail("TRANSFORM_INVALID", "Display dimensions are inconsistent with the display rotation.", s.SourceId);
                if (s.AreaM2 != Area((Int128)w * len)) Fail("METRIC_MISMATCH", "Source areaM2 is wrong.", s.SourceId);
                if (!result.TryAdd(s.SourceId, new Src(s, w, len, s.PatternAxis, s.Kind == SourceKind.NewRoll, offer, s.DisplayRotationDegrees)))
                    Fail("DUPLICATE_SOURCE", "Duplicate sourceId.", s.SourceId);
            }

            if (rolls > 1) Fail("MULTIPLE_NEW_ROLLS", $"{rolls} new rolls in one plan; at most one is allowed.");
            return result;
        }

        private Dictionary<string, Plc> CheckPlacements(Dictionary<string, Src> sources)
        {
            var expected = new Dictionary<string, PieceRequest>(StringComparer.Ordinal);
            foreach (var piece in input.Pieces ?? [])
            {
                if (piece is null) continue;
                for (int k = 1; k <= piece.Quantity; k++) expected[$"{piece.Id}#{k}"] = piece;
            }

            var result = new Dictionary<string, Plc>(StringComparer.Ordinal);
            var mentioned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pl in plan.Placements)
            {
                if (pl is null)
                {
                    Fail("SCHEMA", "Null placement.");
                    continue;
                }

                string id = pl.PieceInstanceId ?? "";
                mentioned.Add(id);
                if (!expected.TryGetValue(id, out var piece))
                {
                    Fail("UNKNOWN_INSTANCE", $"Placement '{id}' does not correspond to a requested instance.", pl.SourceId, id);
                    continue;
                }

                if (result.ContainsKey(id))
                {
                    Fail("DUPLICATE_INSTANCE", $"Instance '{id}' is placed more than once.", pl.SourceId, id);
                    continue;
                }

                if (pl.PieceId != piece.Id) Fail("PIECE_MISMATCH", "pieceId does not match the instance id.", pl.SourceId, id);
                if (!sources.TryGetValue(pl.SourceId ?? "", out var src))
                {
                    Fail("UNKNOWN_SOURCE", $"Placement references unknown source '{pl.SourceId}'.", pl.SourceId, id);
                    continue;
                }

                var pad = piece.PaddingMm ?? new Padding();
                var ppad = pl.PaddingMm ?? new Padding();
                if (pl.NominalWidthMm != piece.WidthMm || pl.NominalLengthMm != piece.LengthMm)
                    Fail("PIECE_MISMATCH", "Nominal dimensions differ from the request.", pl.SourceId, id);
                if (ppad.Top != pad.Top || ppad.Right != pad.Right || ppad.Bottom != pad.Bottom || ppad.Left != pad.Left)
                    Fail("PIECE_MISMATCH", "Padding differs from the request.", pl.SourceId, id);

                bool rot = pl.PieceRotated;
                if (pl.PieceRotationDegrees != (rot ? 90 : 0)) Fail("TRANSFORM_INVALID", "pieceRotationDegrees is inconsistent with pieceRotated.", pl.SourceId, id);
                if (pl.SourceDisplayRotationDegrees != src.DisplayRotation) Fail("TRANSFORM_INVALID", "Placement source display rotation differs from its source.", pl.SourceId, id);

                if (!U(piece.WidthMm, "nominal width", out long nw, pl.SourceId, id) | !U(piece.LengthMm, "nominal length", out long nl, pl.SourceId, id)
                    | !U(pad.Top, "padding.top", out long t, pl.SourceId, id) | !U(pad.Right, "padding.right", out long r, pl.SourceId, id)
                    | !U(pad.Bottom, "padding.bottom", out long b, pl.SourceId, id) | !U(pad.Left, "padding.left", out long l, pl.SourceId, id)
                    | !RectU(pl.Footprint, "footprint", out var f, pl.SourceId, id) | !RectU(pl.NominalRect, "nominalRect", out var nr, pl.SourceId, id))
                    continue;

                // Panel footprint: width = nominal width + left + right, length = nominal length + top + bottom.
                long panelW = nw + l + r, panelL = nl + t + b;
                long expW = rot ? panelL : panelW, expH = rot ? panelW : panelL;
                if (f.W != expW || f.H != expH)
                    Fail("FOOTPRINT_MISMATCH", $"Footprint {Mm(f.W)}×{Mm(f.H)} mm should be {Mm(expW)}×{Mm(expH)} mm.", pl.SourceId, id);
                if (pl.CutWidthMm != Mm(panelW) || pl.CutLengthMm != Mm(panelL))
                    Fail("CUTOUT_SIZE_MISMATCH", $"Cut-out is reported as {pl.CutWidthMm}×{pl.CutLengthMm} mm but padding gives {Mm(panelW)}×{Mm(panelL)} mm.", pl.SourceId, id);

                // Quarter turn clockwise: the panel's left edge becomes the top, its bottom edge becomes the left.
                long offX = rot ? b : l, offY = rot ? l : t;
                long nomW = rot ? nl : nw, nomH = rot ? nw : nl;
                if (nr != (f.X + offX, f.Y + offY, nomW, nomH))
                    Fail("NOMINAL_RECT_MISMATCH", "Nominal panel rectangle is not where the transformed padding puts it.", pl.SourceId, id);

                var finished = Finished(src.Axis, rot);
                if (input.MaterialDirectional && piece.Orientation != PatternOrientation.None && finished != piece.Orientation)
                    Fail("ORIENTATION_VIOLATION", $"Finished pattern would be {finished}, required {piece.Orientation}.", pl.SourceId, id);
                var reported = input.MaterialDirectional ? finished : PatternOrientation.None;
                if (pl.FinishedPatternDirection != reported) Fail("ORIENTATION_VIOLATION", "Reported finished pattern direction is wrong.", pl.SourceId, id);
                if (pl.RequiredOrientation != piece.Orientation) Fail("PIECE_MISMATCH", "Reported required orientation differs from the request.", pl.SourceId, id);
                if (pl.SourcePatternAxis != src.Axis) Fail("TRANSFORM_INVALID", "Reported source pattern axis differs from the source.", pl.SourceId, id);

                if (f.W <= 0 || f.H <= 0 || f.X < 0 || f.Y < 0 || f.X + f.W > src.W || f.Y + f.H > src.L)
                    Fail("OUT_OF_BOUNDS", "Footprint lies outside the source.", pl.SourceId, id);

                result[id] = new Plc(pl, piece, f.X, f.Y, f.W, f.H);
            }

            foreach (var id in expected.Keys)
                if (!mentioned.Contains(id))
                    Fail("MISSING_INSTANCE", $"Requested instance '{id}' is not placed.", piece: id);
            return result;
        }

        private void CheckOverlap(Dictionary<string, Src> sources, Dictionary<string, Plc> placements)
        {
            foreach (var group in placements.Values.GroupBy(p => p.Dto.SourceId))
            {
                var list = group.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    for (int j = i + 1; j < list.Count && list[j].X < a.X + a.W; j++)
                    {
                        var c = list[j];
                        if (c.Y < a.Y + a.H && a.Y < c.Y + c.H)
                            Fail("OVERLAP", $"Instances '{a.Dto.PieceInstanceId}' and '{c.Dto.PieceInstanceId}' overlap.", group.Key, a.Dto.PieceInstanceId);
                    }
                }
            }
        }

        private Dictionary<string, Res> CollectResiduals(Dictionary<string, Src> sources)
        {
            var result = new Dictionary<string, Res>(StringComparer.Ordinal);
            foreach (var r in plan.PredictedResiduals)
            {
                if (r is null)
                {
                    Fail("SCHEMA", "Null residual.");
                    continue;
                }

                if (!sources.ContainsKey(r.SourceId ?? ""))
                {
                    Fail("RESIDUAL_INVALID", "Residual references an unknown source.", r.SourceId);
                    continue;
                }

                if (!RectU(r.Region, "residual region", out var u, r.SourceId)) continue;
                if (u.W <= 0 || u.H <= 0) Fail("RESIDUAL_INVALID", "Residual must have positive dimensions.", r.SourceId);
                if (!result.TryAdd(r.ResidualId ?? "", new Res(r, u.X, u.Y, u.W, u.H)))
                    Fail("RESIDUAL_INVALID", $"Duplicate residual id '{r.ResidualId}'.", r.SourceId);
                if (r.AreaM2 != Area((Int128)u.W * u.H)) Fail("METRIC_MISMATCH", $"Residual '{r.ResidualId}' area is wrong.", r.SourceId);
                var (eligible, _) = Eligibility(u.W, u.H);
                if (r.Eligible != eligible) Fail("RESIDUAL_INVALID", $"Residual '{r.ResidualId}' eligibility does not follow the remnant policy.", r.SourceId);
                if (string.IsNullOrWhiteSpace(r.Reason)) Fail("RESIDUAL_INVALID", "Residual eligibility reason is missing.", r.SourceId);
            }

            return result;
        }

        private (Dictionary<string, (string Source, CutTreeNode Node, (long X, long Y, long W, long H) Region)> Cuts, Dictionary<string, string> ParentOf, Dictionary<string, int> CutsBySource)
            CheckTrees(Dictionary<string, Src> sources, Dictionary<string, Plc> placements, Dictionary<string, Res> residuals)
        {
            var cuts = new Dictionary<string, (string, CutTreeNode, (long, long, long, long))>(StringComparer.Ordinal);
            var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var cutsBySource = new Dictionary<string, int>(StringComparer.Ordinal);
            var coveredPieces = new HashSet<string>(StringComparer.Ordinal);
            var coveredResiduals = new HashSet<string>(StringComparer.Ordinal);
            var treeSeen = new HashSet<string>(StringComparer.Ordinal);
            int totalNodes = 0;

            foreach (var tree in plan.CutTrees)
            {
                if (tree is null || !sources.TryGetValue(tree.SourceId ?? "", out var src))
                {
                    Fail("TREE_INVALID", "Cut tree references an unknown source.", tree?.SourceId);
                    continue;
                }

                if (!treeSeen.Add(tree.SourceId!))
                {
                    Fail("TREE_INVALID", "Source has more than one cut tree.", tree.SourceId);
                    continue;
                }

                string sid = tree.SourceId!;
                totalNodes += tree.Nodes?.Count ?? 0;
                if (tree.Nodes is null || totalNodes > MaxNodes)
                {
                    Fail("TREE_INVALID", "Cut tree has no nodes or too many nodes.", sid);
                    continue;
                }

                var byId = new Dictionary<string, CutTreeNode>(StringComparer.Ordinal);
                foreach (var n in tree.Nodes)
                {
                    if (n is null || string.IsNullOrEmpty(n.NodeId) || !byId.TryAdd(n.NodeId, n))
                        Fail("DUPLICATE_NODE", $"Missing or duplicate node id '{n?.NodeId}'.", sid, node: n?.NodeId);
                }

                if (!byId.ContainsKey(tree.RootNodeId ?? ""))
                {
                    Fail("TREE_ROOT_MISSING", "Root node not found.", sid);
                    continue;
                }

                int cutCount = 0;
                Int128 leafArea = 0;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var stack = new Stack<(string Id, (long X, long Y, long W, long H) Expect)>();
                stack.Push((tree.RootNodeId!, (0, 0, src.W, src.L)));
                while (stack.Count > 0)
                {
                    var (id, expect) = stack.Pop();
                    if (!visited.Add(id))
                    {
                        Fail("TREE_CYCLE", $"Node '{id}' is reachable more than once (cycle or shared child).", sid, node: id);
                        continue;
                    }

                    if (!byId.TryGetValue(id, out var node))
                    {
                        Fail("TREE_DANGLING", $"Child '{id}' does not exist.", sid, node: id);
                        continue;
                    }

                    if (!RectU(node.Region, "node region", out var reg, sid, node: id)) continue;
                    if (reg != expect)
                    {
                        Fail(id == tree.RootNodeId ? "TREE_ROOT_MISMATCH" : "CHILD_REGION_MISMATCH",
                            $"Node region ({Mm(reg.X)},{Mm(reg.Y)},{Mm(reg.W)}×{Mm(reg.H)}) must be ({Mm(expect.X)},{Mm(expect.Y)},{Mm(expect.W)}×{Mm(expect.H)}).", sid, node: id);
                        continue;
                    }

                    switch (node.Kind)
                    {
                        case CutNodeKind.Cut:
                        {
                            if (node.Axis is not CutAxis axis || node.PositionMm is not decimal posMm || node.FirstChildId is null || node.SecondChildId is null
                                || node.PieceInstanceId is not null || node.ResidualId is not null)
                            {
                                Fail("ILLEGAL_CUT", "Cut node needs an axis, a position and exactly two children.", sid, node: id);
                                continue;
                            }

                            if (!U(posMm, "cut position", out long pos, sid, node: id)) continue;
                            (long X, long Y, long W, long H) first, second;
                            if (axis == CutAxis.Vertical)
                            {
                                if (pos <= reg.X || pos >= reg.X + reg.W)
                                {
                                    Fail("ILLEGAL_CUT", "Vertical cut does not lie strictly inside its region.", sid, node: id);
                                    continue;
                                }

                                first = (reg.X, reg.Y, pos - reg.X, reg.H);
                                second = (pos, reg.Y, reg.X + reg.W - pos, reg.H);
                            }
                            else
                            {
                                if (pos <= reg.Y || pos >= reg.Y + reg.H)
                                {
                                    Fail("ILLEGAL_CUT", "Horizontal cut does not lie strictly inside its region.", sid, node: id);
                                    continue;
                                }

                                first = (reg.X, reg.Y, reg.W, pos - reg.Y);
                                second = (reg.X, pos, reg.W, reg.Y + reg.H - pos);
                            }

                            cutCount++;
                            cuts[id] = (sid, node, reg);
                            parentOf[node.FirstChildId] = id;
                            parentOf[node.SecondChildId] = id;
                            stack.Push((node.SecondChildId, second));
                            stack.Push((node.FirstChildId, first));
                            break;
                        }
                        case CutNodeKind.Piece:
                        {
                            if (node.FirstChildId is not null || node.SecondChildId is not null || node.Axis is not null)
                                Fail("LEAF_MISMATCH", "Piece leaf must not have children or a cut.", sid, node: id);
                            string pid = node.PieceInstanceId ?? "";
                            if (!placements.TryGetValue(pid, out var pl) || pl.Dto.SourceId != sid)
                            {
                                Fail("LEAF_MISMATCH", $"Piece leaf references '{pid}', which is not placed on this source.", sid, pid, id);
                                continue;
                            }

                            if (!coveredPieces.Add(pid)) Fail("LEAF_MISMATCH", $"Instance '{pid}' appears in more than one leaf.", sid, pid, id);
                            if ((pl.X, pl.Y, pl.W, pl.H) != reg) Fail("LEAF_MISMATCH", "Piece leaf region differs from the placement footprint.", sid, pid, id);
                            leafArea += reg.W * reg.H;
                            break;
                        }
                        case CutNodeKind.Residual:
                        {
                            if (node.FirstChildId is not null || node.SecondChildId is not null || node.Axis is not null)
                                Fail("LEAF_MISMATCH", "Residual leaf must not have children or a cut.", sid, node: id);
                            string rid = node.ResidualId ?? "";
                            if (!residuals.TryGetValue(rid, out var res) || res.Dto.SourceId != sid)
                            {
                                Fail("LEAF_MISMATCH", $"Residual leaf references unknown residual '{rid}'.", sid, node: id);
                                continue;
                            }

                            if (!coveredResiduals.Add(rid)) Fail("LEAF_MISMATCH", $"Residual '{rid}' appears in more than one leaf.", sid, node: id);
                            if ((res.X, res.Y, res.W, res.H) != reg) Fail("LEAF_MISMATCH", "Residual leaf region differs from the predicted residual.", sid, node: id);
                            leafArea += reg.W * reg.H;
                            break;
                        }
                        default:
                            Fail("SCHEMA", "Unknown node kind.", sid, node: id);
                            break;
                    }
                }

                foreach (var id in byId.Keys)
                    if (!visited.Contains(id)) Fail("TREE_ORPHAN", $"Node '{id}' is not connected to the root.", sid, node: id);
                if (leafArea != (Int128)src.W * src.L)
                    Fail("AREA_CONSERVATION", "Leaf areas do not exactly partition the source.", sid);
                cutsBySource[sid] = cutCount;
            }

            foreach (var sid in sources.Keys)
                if (!treeSeen.Contains(sid)) Fail("TREE_MISSING", "Source has no cut tree.", sid);
            foreach (var id in placements.Keys)
                if (!coveredPieces.Contains(id)) Fail("UNCOVERED_PLACEMENT", $"Instance '{id}' is not a leaf of its source's cut tree.", placements[id].Dto.SourceId, id);
            foreach (var id in residuals.Keys)
                if (!coveredResiduals.Contains(id)) Fail("UNCOVERED_RESIDUAL", $"Residual '{id}' is not a leaf of any cut tree.", residuals[id].Dto.SourceId);
            foreach (var src in sources.Values)
                if (!placements.Values.Any(p => p.Dto.SourceId == src.Dto.SourceId))
                    Fail("UNUSED_SOURCE", "A listed source holds no piece; untouched stock must not be committed.", src.Dto.SourceId);
            return (cuts, parentOf, cutsBySource);
        }

        private void CheckCutSteps(Dictionary<string, (string Source, CutTreeNode Node, (long X, long Y, long W, long H) Region)> cuts, Dictionary<string, string> parentOf)
        {
            var seqOf = new Dictionary<string, int>(StringComparer.Ordinal);
            int expectedSeq = 1;
            foreach (var step in plan.CutSteps)
            {
                if (step is null)
                {
                    Fail("CUT_STEPS_INVALID", "Null cut step.");
                    continue;
                }

                if (step.Sequence != expectedSeq++) Fail("CUT_STEPS_INVALID", "Cut step sequence numbers must be 1..n in order.", step.SourceId, node: step.NodeId);
                if (!cuts.TryGetValue(step.NodeId ?? "", out var cut) || cut.Source != step.SourceId)
                {
                    Fail("CUT_STEPS_INVALID", $"Cut step references unknown cut node '{step.NodeId}'.", step.SourceId, node: step.NodeId);
                    continue;
                }

                if (!seqOf.TryAdd(step.NodeId!, step.Sequence))
                {
                    Fail("CUT_STEPS_INVALID", "Cut node appears in more than one step.", step.SourceId, node: step.NodeId);
                    continue;
                }

                var (x, y, w, h) = cut.Region;
                bool vertical = cut.Node.Axis == CutAxis.Vertical;
                decimal pos = cut.Node.PositionMm!.Value;
                bool geometryOk = step.Axis == cut.Node.Axis && step.PositionMm == pos
                    && step.Region == cut.Node.Region
                    && step.FromXMm == (vertical ? pos : Mm(x)) && step.FromYMm == (vertical ? Mm(y) : pos)
                    && step.ToXMm == (vertical ? pos : Mm(x + w)) && step.ToYMm == (vertical ? Mm(y + h) : pos);
                if (!geometryOk) Fail("CUT_STEPS_INVALID", "Cut step geometry does not match its edge-to-edge cut node.", step.SourceId, node: step.NodeId);
                if (parentOf.TryGetValue(step.NodeId!, out var parent) && (!seqOf.TryGetValue(parent, out int ps) || ps >= step.Sequence))
                    Fail("CUT_STEPS_INVALID", "A cut is scheduled before the cut that creates its region.", step.SourceId, node: step.NodeId);
            }

            foreach (var id in cuts.Keys)
                if (!seqOf.ContainsKey(id)) Fail("CUT_STEPS_INVALID", $"Cut node '{id}' has no execution step.", cuts[id].Source, node: id);
        }

        private void CheckPurchase(Dictionary<string, Src> sources, Dictionary<string, Plc> placements, Dictionary<string, Res> residuals)
        {
            var roll = sources.Values.FirstOrDefault(s => s.IsRoll);
            foreach (var r in residuals.Values)
                if (r.Dto.RoundingSurplus && (roll is null || r.Dto.SourceId != roll.Dto.SourceId))
                    Fail("SURPLUS_INVALID", "Only a new roll can have a rounding-surplus residual.", r.Dto.SourceId);

            if (roll is null)
            {
                if (plan.Purchase is not null || plan.RequiresPurchase) Fail("PURCHASE_INVALID", "Plan has no new roll but declares a purchase.");
                return;
            }

            var offer = roll.Offer!;
            var pu = plan.Purchase;
            if (pu is null || !plan.RequiresPurchase)
            {
                Fail("PURCHASE_INVALID", "Plan uses a new roll but has no purchase line.", roll.Dto.SourceId);
                return;
            }

            long consumed = placements.Values.Where(p => p.Dto.SourceId == roll.Dto.SourceId).Select(p => p.Y + p.H).DefaultIfEmpty(0).Max();
            long metre = 1000 * _scale;
            decimal orderedMetres;
            if (offer.FixedLengthMm is decimal fixedMm)
            {
                if (roll.Dto.LengthMm != fixedMm) Fail("ORDER_LENGTH_INVALID", "Fixed-length stock must keep its fixed length.", roll.Dto.SourceId);
                if (!pu.FixedLength) Fail("PURCHASE_INVALID", "Purchase must be flagged as fixed length.", roll.Dto.SourceId);
                orderedMetres = fixedMm / 1000m;
            }
            else
            {
                if (pu.FixedLength) Fail("PURCHASE_INVALID", "Purchase is wrongly flagged as fixed length.", roll.Dto.SourceId);
                if (roll.L % metre != 0)
                {
                    Fail("ORDER_LENGTH_INVALID", "Ordered roll length is not a whole number of metres.", roll.Dto.SourceId);
                    return;
                }

                long m = roll.L / metre;
                long need = (consumed + metre - 1) / metre;
                long smallest = offer.MinOrderMetres;
                if (need > smallest) smallest += (need - offer.MinOrderMetres + offer.OrderIncrementMetres - 1) / offer.OrderIncrementMetres * offer.OrderIncrementMetres;
                if (m < offer.MinOrderMetres || (m - offer.MinOrderMetres) % offer.OrderIncrementMetres != 0 || (offer.MaxOrderMetres is int max && m > max))
                    Fail("ORDER_LENGTH_INVALID", $"{m} m is not a permitted order length for offer '{offer.OfferId}'.", roll.Dto.SourceId);
                else if (m != smallest)
                    Fail("ORDER_LENGTH_INVALID", $"Ordered {m} m but the smallest permitted length covering {Mm(consumed)} mm is {smallest} m.", roll.Dto.SourceId);
                orderedMetres = m;
            }

            if (consumed > roll.L) Fail("ORDER_LENGTH_INVALID", "Layout extends beyond the ordered length.", roll.Dto.SourceId);
            decimal area = Area((Int128)roll.W * roll.L);
            if (pu.PurchaseCostRounded != Money.Round(area * offer.PricePerSquareMetre) || pu.CostRounding != Money.RoundingRule)
                Fail("PURCHASE_INVALID", $"The rounded purchase cost must be the exact cost rounded {Money.RoundingRule}.", roll.Dto.SourceId);
            if (pu.RollOfferId != offer.OfferId || pu.OrderedWidthMm != offer.WidthMm || pu.OrderedLengthMetres != orderedMetres
                || pu.ConsumedLengthMm != Mm(consumed) || pu.RoundingSurplusMm != Mm(roll.L - consumed) || pu.PurchasedAreaM2 != area
                || pu.PricePerSquareMetre != offer.PricePerSquareMetre || pu.PurchaseCost != area * offer.PricePerSquareMetre || pu.Currency != offer.Currency)
                Fail("PURCHASE_INVALID", "Purchase line does not match the offer snapshot and recomputed layout.", roll.Dto.SourceId);

            if (CompactedLengths(roll.Dto.SourceId, placements) is var (padded, nominal))
            {
                long paddingShare = padded - nominal;
                if (pu.PaddingLengthMm != Mm(paddingShare) || pu.LengthWithoutPaddingMm != Mm(consumed - paddingShare))
                    Fail("PURCHASE_INVALID", $"Length breakdown should be {Mm(consumed - paddingShare)} mm + {Mm(paddingShare)} mm padding.", roll.Dto.SourceId);
            }

            var flagged = residuals.Values.Where(r => r.Dto.RoundingSurplus).ToList();
            if (roll.L > consumed)
            {
                if (flagged.Count != 1 || (flagged[0].X, flagged[0].Y, flagged[0].W, flagged[0].H) != (0, consumed, roll.W, roll.L - consumed))
                    Fail("SURPLUS_INVALID", $"Exactly one full-width rounding-surplus residual {Mm(roll.W)}×{Mm(roll.L - consumed)} mm at y={Mm(consumed)} is required.", roll.Dto.SourceId);
            }
            else if (flagged.Count != 0)
            {
                Fail("SURPLUS_INVALID", "No rounding surplus exists, yet a residual is flagged as surplus.", roll.Dto.SourceId);
            }
        }

        /// <summary>
        /// Compacted length of a source's cut tree along y: cuts across the width stack their children, cuts along
        /// the length keep the longer child, residual leaves need no length. Computed once with padded footprints and
        /// once with finished panels (a quarter-turned panel lies with its finished width along the roll).
        /// Returns null when the tree is unusable (already reported by the tree replay).
        /// </summary>
        private (long Padded, long Nominal)? CompactedLengths(string sourceId, Dictionary<string, Plc> placements)
        {
            var tree = plan.CutTrees.FirstOrDefault(t => t?.SourceId == sourceId);
            if (tree?.Nodes is null || tree.RootNodeId is null) return null;
            var byId = new Dictionary<string, CutTreeNode>(StringComparer.Ordinal);
            foreach (var n in tree.Nodes)
                if (n?.NodeId is not null) byId.TryAdd(n.NodeId, n);

            var done = new Dictionary<string, (long Padded, long Nominal)>(StringComparer.Ordinal);
            var stack = new Stack<(string Id, bool ChildrenDone)>();
            stack.Push((tree.RootNodeId, false));
            int steps = 0;
            while (stack.Count > 0)
            {
                if (++steps > 4 * byId.Count + 4) return null; // cycle guard
                var (id, childrenDone) = stack.Pop();
                if (!byId.TryGetValue(id, out var node)) return null;
                switch (node.Kind)
                {
                    case CutNodeKind.Cut when node.FirstChildId is null || node.SecondChildId is null:
                        return null;
                    case CutNodeKind.Cut when !childrenDone:
                        stack.Push((id, true));
                        stack.Push((node.SecondChildId, false));
                        stack.Push((node.FirstChildId, false));
                        break;
                    case CutNodeKind.Cut:
                    {
                        if (!done.TryGetValue(node.FirstChildId, out var a) || !done.TryGetValue(node.SecondChildId, out var b)) return null;
                        done[id] = node.Axis == CutAxis.Vertical
                            ? (Math.Max(a.Padded, b.Padded), Math.Max(a.Nominal, b.Nominal))
                            : (a.Padded + b.Padded, a.Nominal + b.Nominal);
                        break;
                    }
                    case CutNodeKind.Piece:
                    {
                        if (!placements.TryGetValue(node.PieceInstanceId ?? "", out var pl)) return null;
                        decimal finishedAlongRoll = pl.Dto.PieceRotated ? pl.Piece.WidthMm : pl.Piece.LengthMm;
                        if (!U(finishedAlongRoll, "finished length along roll", out long nominal)) return null;
                        done[id] = (pl.H, nominal);
                        break;
                    }
                    default:
                        done[id] = (0, 0);
                        break;
                }
            }

            return done.TryGetValue(tree.RootNodeId, out var root) ? root : null;
        }

        private void CheckMetrics(Dictionary<string, Src> sources, Dictionary<string, Plc> placements, Dictionary<string, Res> residuals, int cuts)
        {
            var m = plan.Metrics;
            Int128 req = Total(placements.Values.Select(p => p.W * p.H));
            Int128 purchased = Total(sources.Values.Where(s => s.IsRoll).Select(s => s.W * s.L));
            Int128 stock = Total(sources.Values.Where(s => !s.IsRoll).Select(s => s.W * s.L));
            Int128 residual = Total(residuals.Values.Select(r => r.W * r.H));
            if (purchased + stock != req + residual)
                Fail("AREA_CONSERVATION", "A_new + A_stock_used must equal A_req + A_residual.");

            Int128 reusable = 0, warehouseReusable = 0;
            long largestReusable = 0, largestLeaf = 0;
            long? minDim = null;
            int thin = 0;
            var offcuts = new List<(long W, long H)>();
            foreach (var r in residuals.Values)
            {
                var (eligible, isThin) = Eligibility(r.W, r.H);
                long a = r.W * r.H;
                largestLeaf = Math.Max(largestLeaf, a);
                if (isThin) thin++;
                if (!eligible) continue;
                reusable += a;
                if (sources.TryGetValue(r.Dto.SourceId ?? "", out var rs) && !rs.IsRoll) warehouseReusable += a;
                largestReusable = Math.Max(largestReusable, a);
                minDim = Math.Min(minDim ?? long.MaxValue, Math.Min(r.W, r.H));
                offcuts.Add((r.W, r.H));
            }

            var roll = sources.Values.FirstOrDefault(s => s.IsRoll);
            decimal cost = roll is null ? 0 : Area((Int128)roll.W * roll.L) * roll.Offer!.PricePerSquareMetre;
            long? consumed = roll is null ? null : placements.Values.Where(p => p.Dto.SourceId == roll.Dto.SourceId).Select(p => p.Y + p.H).DefaultIfEmpty(0).Max();

            // Everything the snapshot offered from the warehouse, used or not (re-derived from the input, not the plan).
            Int128 offered = 0;
            foreach (var st in input.Stock ?? [])
                if (st is not null && U(st.WidthMm, "stock width", out long sw, st.StockId) && U(st.LengthMm, "stock length", out long sl, st.StockId))
                    offered += (Int128)sw * sl;

            void Eq<T>(string field, T actual, T expected)
            {
                if (!EqualityComparer<T>.Default.Equals(actual, expected))
                    Fail("METRIC_MISMATCH", $"metrics.{field} = {actual}, recomputed {expected}.");
            }

            Eq(nameof(m.RequiredCutAreaM2), m.RequiredCutAreaM2, Area(req));
            Eq(nameof(m.PurchasedAreaM2), m.PurchasedAreaM2, Area(purchased));
            Eq(nameof(m.NewConsumedAreaM2), m.NewConsumedAreaM2, roll is null ? 0m : Area((Int128)roll.W * consumed!.Value));
            Eq(nameof(m.RoundingSurplusAreaM2), m.RoundingSurplusAreaM2, roll is null ? 0m : Area((Int128)roll.W * (roll.L - consumed!.Value)));
            Eq(nameof(m.ExistingStockUntouchedAreaM2), m.ExistingStockUntouchedAreaM2, Area(offered - stock));
            Eq(nameof(m.StockDepletionAreaM2), m.StockDepletionAreaM2, Area(stock - warehouseReusable));
            Eq(nameof(m.PurchaseCost), m.PurchaseCost, cost);
            Eq(nameof(m.Currency), m.Currency, roll?.Offer?.Currency);
            Eq(nameof(m.ExistingStockAreaConsumedM2), m.ExistingStockAreaConsumedM2, Area(stock));
            Eq(nameof(m.TotalResidualAreaM2), m.TotalResidualAreaM2, Area(residual));
            Eq(nameof(m.ReusableResidualAreaM2), m.ReusableResidualAreaM2, Area(reusable));
            Eq(nameof(m.UnusableScrapAreaM2), m.UnusableScrapAreaM2, Area(residual - reusable));
            Eq(nameof(m.ReusableOffcutCount), m.ReusableOffcutCount, offcuts.Count);
            Eq(nameof(m.LargestReusableAreaM2), m.LargestReusableAreaM2, Area(largestReusable));
            Eq(nameof(m.MinReusableDimensionMm), m.MinReusableDimensionMm, minDim is long d ? Mm(d) : null);
            Eq(nameof(m.FragmentCount), m.FragmentCount, residuals.Count);
            Eq(nameof(m.ThinFragmentCount), m.ThinFragmentCount, thin);
            Eq(nameof(m.FragmentationIndex), m.FragmentationIndex, residual == 0 ? 0m : Math.Round(1m - (decimal)largestLeaf / (decimal)residual, 6));
            Eq(nameof(m.ConsumedLengthMm), m.ConsumedLengthMm, consumed is long c ? Mm(c) : null);
            Eq(nameof(m.OrderedLengthMetres), m.OrderedLengthMetres, roll is null ? null : (decimal)roll.L / (1000 * _scale));
            Eq(nameof(m.RoundingSurplusMm), m.RoundingSurplusMm, roll is null ? null : Mm(roll.L - consumed!.Value));
            Eq(nameof(m.TotalCuts), m.TotalCuts, cuts);
            Eq(nameof(m.SourcesUsed), m.SourcesUsed, sources.Count);
            Eq(nameof(m.WarehouseSourcesUsed), m.WarehouseSourcesUsed, sources.Values.Count(s => !s.IsRoll));
            Eq(nameof(m.MaterialUtilization), m.MaterialUtilization, purchased + stock == 0 ? 0m : Math.Round((decimal)req / (decimal)(purchased + stock), 6));

            // The material ledger (handbook §96) must balance on the reported figures themselves.
            if (m.PurchasedAreaM2 + m.ExistingStockAreaConsumedM2 != m.RequiredCutAreaM2 + m.ReusableResidualAreaM2 + m.UnusableScrapAreaM2)
                Fail("LEDGER_MISMATCH", "New ordered + existing stock consumed must equal required + usable leftovers + scrap.");
            if (m.PurchasedAreaM2 != m.NewConsumedAreaM2 + m.RoundingSurplusAreaM2)
                Fail("LEDGER_MISMATCH", "New ordered must equal new consumed + rounding surplus.");
            if (m.ExistingStockAreaConsumedM2 + m.ExistingStockUntouchedAreaM2 != Area(offered))
                Fail("LEDGER_MISMATCH", "Existing stock consumed + untouched must equal the warehouse stock the snapshot offered.");

            var reported = new List<(long, long)>();
            foreach (var o in m.ReusableOffcuts ?? [])
            {
                if (o is null || !U(o.Width, "offcut width", out long ow) || !U(o.Length, "offcut length", out long ol)) return;
                reported.Add((ow, ol));
            }

            if (!reported.Order().SequenceEqual(offcuts.Order()))
                Fail("METRIC_MISMATCH", "metrics.reusableOffcuts does not list the eligible residuals.");
        }
    }
}

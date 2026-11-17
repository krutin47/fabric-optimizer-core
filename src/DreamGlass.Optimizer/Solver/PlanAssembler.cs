using System.Text;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

/// <summary>
/// Converts a <see cref="FinalPlan"/> into the output contract. All metrics are recomputed from the final trees
/// (spec §6.7), never from heuristic counters.
/// </summary>
internal static class PlanAssembler
{
    /// <summary>
    /// Roll length attributable to padding for this arrangement: compact the cut tree along the roll (vertical cuts
    /// take the longer side, horizontal cuts stack, residuals take no length) once with padded footprints and once
    /// with finished panels, and subtract.
    /// </summary>
    internal static long PaddingLength(Node root, NormalizedProblem p, bool[] rotated)
    {
        var order = Node.PreOrder(root).ToList();
        var len = new Dictionary<Node, (long Padded, long Nominal)>(ReferenceEqualityComparer.Instance);
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            if (n.A is not null)
            {
                var (pa, na) = len[n.A];
                var (pb, nb) = len[n.B!];
                len[n] = n.Axis == CutAxis.Vertical ? (Math.Max(pa, pb), Math.Max(na, nb)) : (pa + pb, na + nb);
            }
            else if (n.Piece >= 0)
            {
                var t = p.TypeOf(n.Piece);
                len[n] = (n.R.H, rotated[n.Piece] ? t.NomW : t.NomL);
            }
            else
            {
                len[n] = (0, 0);
            }
        }

        var (padded, nominal) = len[root];
        return padded - nominal;
    }

    public static CandidateCutPlan Assemble(FinalPlan fp)
    {
        var p = fp.P;
        long sc = p.Scale;
        decimal Mm(long u) => Units.ToMm(u, sc);
        decimal M2(long a) => Units.AreaToM2(a, sc);
        RectMm R(Rect r) => new(Mm(r.X), Mm(r.Y), Mm(r.W), Mm(r.H));

        var sources = new List<PlanSource>();
        var trees = new List<SourceCutTree>();
        var steps = new List<CutStep>();
        var residuals = new List<PredictedResidual>();
        var placements = new List<Placement>();
        var canonical = new StringBuilder();

        long requiredArea = 0, purchasedArea = 0, stockArea = 0, residualArea = 0, reusableArea = 0;
        long largestResidualLeaf = 0, largestReusable = 0;
        long? minReusableDim = null;
        int fragments = 0, thin = 0, cuts = 0, warehouseUsed = 0;
        var offcuts = new List<(long W, long H)>();
        PurchaseLine? purchase = null;
        decimal cost = 0;
        string? currency = null;
        long? consumed = null, ordered = null, surplus = null;

        foreach (var fs in fp.Sources)
        {
            var spec = fs.Spec;
            // Every sheet is drawn with its length left to right, so display width = length and display length = width.
            const int displayRot = OrientationRules.SheetDisplayRotationDegrees;
            long srcArea = spec.W * fs.Length;
            sources.Add(new PlanSource
            {
                SourceId = spec.SourceId,
                Kind = spec.Kind,
                StockId = spec.Stock?.StockId,
                StockVersion = spec.Stock?.Version,
                RollOfferId = spec.Offer?.OfferId,
                BinReference = spec.Stock?.BinReference,
                WidthMm = Mm(spec.W),
                LengthMm = Mm(fs.Length),
                PatternAxis = spec.Axis,
                DisplayRotationDegrees = displayRot,
                DisplayWidthMm = Mm(fs.Length),
                DisplayLengthMm = Mm(spec.W),
                AreaM2 = M2(srcArea),
            });
            canonical.Append("S|").Append(spec.SourceId).Append('|').Append(fs.Length).Append(';');

            if (spec.IsRoll)
            {
                purchasedArea += srcArea;
                consumed = fs.Consumed;
                ordered = fs.OrderedLength;
                surplus = fs.Length - fs.Consumed;
                var offer = spec.Offer!;
                currency = offer.Currency;
                cost = M2(srcArea) * offer.PricePerSquareMetre;
                purchase = new PurchaseLine
                {
                    RollOfferId = offer.OfferId,
                    OrderedWidthMm = Mm(spec.W),
                    OrderedLengthMetres = (decimal)fs.Length / spec.MetreUnits,
                    ConsumedLengthMm = Mm(fs.Consumed),
                    PaddingLengthMm = Mm(PaddingLength(fs.Root, p, fp.Rotated)),
                    LengthWithoutPaddingMm = Mm(fs.Consumed - PaddingLength(fs.Root, p, fp.Rotated)),
                    RoundingSurplusMm = Mm(fs.Length - fs.Consumed),
                    PurchasedAreaM2 = M2(srcArea),
                    PricePerSquareMetre = offer.PricePerSquareMetre,
                    PurchaseCost = cost,
                    Currency = offer.Currency,
                    FixedLength = spec.FixedLengthUnits is not null,
                };
            }
            else
            {
                stockArea += srcArea;
                warehouseUsed++;
            }

            var nodes = new List<CutTreeNode>();
            var order = Node.PreOrder(fs.Root).ToList();
            var ids = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < order.Count; k++) ids[order[k]] = $"{spec.SourceId}/n{k + 1}";
            int residualSeq = 0;
            foreach (var node in order)
            {
                var r = node.R;
                if (node.A is not null)
                {
                    cuts++;
                    bool vertical = node.Axis == CutAxis.Vertical;
                    nodes.Add(new CutTreeNode
                    {
                        NodeId = ids[node],
                        Kind = CutNodeKind.Cut,
                        Region = R(r),
                        Axis = node.Axis,
                        PositionMm = Mm(node.Pos),
                        FirstChildId = ids[node.A],
                        SecondChildId = ids[node.B!],
                    });
                    steps.Add(new CutStep
                    {
                        Sequence = steps.Count + 1,
                        SourceId = spec.SourceId,
                        NodeId = ids[node],
                        Axis = node.Axis,
                        PositionMm = Mm(node.Pos),
                        Region = R(r),
                        FromXMm = vertical ? Mm(node.Pos) : Mm(r.X),
                        FromYMm = vertical ? Mm(r.Y) : Mm(node.Pos),
                        ToXMm = vertical ? Mm(node.Pos) : Mm(r.Right),
                        ToYMm = vertical ? Mm(r.Bottom) : Mm(node.Pos),
                    });
                    canonical.Append(vertical ? 'V' : 'H').Append(node.Pos).Append(',');
                }
                else if (node.Piece >= 0)
                {
                    int inst = node.Piece;
                    var t = p.TypeOf(inst);
                    bool rot = fp.Rotated[inst];
                    // Rotated (90° clockwise): source-top = panel-left, right = top, bottom = right, left = bottom.
                    long srcTop = rot ? t.PadL : t.PadT;
                    long srcLeft = rot ? t.PadB : t.PadL;
                    long nomX = rot ? t.NomL : t.NomW;
                    long nomY = rot ? t.NomW : t.NomL;
                    requiredArea += r.Area;
                    string id = fp.InstanceIds[inst];
                    nodes.Add(new CutTreeNode { NodeId = ids[node], Kind = CutNodeKind.Piece, Region = R(r), PieceInstanceId = id });
                    placements.Add(new Placement
                    {
                        PieceInstanceId = id,
                        PieceId = t.PieceId,
                        Label = t.Label,
                        SourceId = spec.SourceId,
                        Footprint = R(r),
                        NominalRect = new RectMm(Mm(r.X + srcLeft), Mm(r.Y + srcTop), Mm(nomX), Mm(nomY)),
                        NominalWidthMm = Mm(t.NomW),
                        NominalLengthMm = Mm(t.NomL),
                        CutWidthMm = Mm(t.FootW),
                        CutLengthMm = Mm(t.FootL),
                        PaddingMm = new Padding { Top = Mm(t.PadT), Right = Mm(t.PadR), Bottom = Mm(t.PadB), Left = Mm(t.PadL) },
                        PieceRotated = rot,
                        PieceRotationDegrees = rot ? 90 : 0,
                        SourceDisplayRotationDegrees = displayRot,
                        FinishedPatternDirection = p.Directional ? OrientationRules.FinishedDirection(spec.Axis, rot) : PatternOrientation.None,
                    });
                    canonical.Append('P').Append(id).Append('@').Append(r.X).Append(',').Append(r.Y).Append(rot ? 'r' : 'u').Append(',');
                }
                else
                {
                    residualSeq++;
                    string rid = $"{spec.SourceId}/r{residualSeq}";
                    var cls = ResidualClassifier.Classify(r.W, r.H, p.Policy, sc);
                    long a = r.Area;
                    residualArea += a;
                    fragments++;
                    if (cls.Thin) thin++;
                    largestResidualLeaf = Math.Max(largestResidualLeaf, a);
                    if (cls.Eligible)
                    {
                        reusableArea += a;
                        largestReusable = Math.Max(largestReusable, a);
                        long shortSide = Math.Min(r.W, r.H);
                        minReusableDim = minReusableDim is long m ? Math.Min(m, shortSide) : shortSide;
                        offcuts.Add((r.W, r.H));
                    }

                    nodes.Add(new CutTreeNode { NodeId = ids[node], Kind = CutNodeKind.Residual, Region = R(r), ResidualId = rid });
                    residuals.Add(new PredictedResidual
                    {
                        ResidualId = rid,
                        SourceId = spec.SourceId,
                        Region = R(r),
                        AreaM2 = M2(a),
                        Eligible = cls.Eligible,
                        Reason = cls.Reason,
                        RoundingSurplus = node.Surplus,
                    });
                    canonical.Append('R').Append(',');
                }
            }

            trees.Add(new SourceCutTree { SourceId = spec.SourceId, RootNodeId = ids[fs.Root], Nodes = nodes });
        }

        if (requiredArea + residualArea != purchasedArea + stockArea)
            throw new InvalidOperationException("Area conservation violated while assembling a plan.");

        placements.Sort((a, b) =>
        {
            int c = sources.FindIndex(s => s.SourceId == a.SourceId).CompareTo(sources.FindIndex(s => s.SourceId == b.SourceId));
            if (c != 0) return c;
            c = a.Footprint.Y.CompareTo(b.Footprint.Y);
            return c != 0 ? c : a.Footprint.X.CompareTo(b.Footprint.X);
        });

        offcuts.Sort((a, b) =>
        {
            int c = (b.W * b.H).CompareTo(a.W * a.H);
            if (c != 0) return c;
            c = b.W.CompareTo(a.W);
            return c != 0 ? c : b.H.CompareTo(a.H);
        });
        var metrics = new PlanMetrics
        {
            RequiredCutAreaM2 = M2(requiredArea),
            PurchasedAreaM2 = M2(purchasedArea),
            PurchaseCost = cost,
            Currency = currency,
            ExistingStockAreaConsumedM2 = M2(stockArea),
            TotalResidualAreaM2 = M2(residualArea),
            ReusableResidualAreaM2 = M2(reusableArea),
            UnusableScrapAreaM2 = M2(residualArea - reusableArea),
            ReusableOffcutCount = offcuts.Count,
            ReusableOffcuts = offcuts.Select(o => new DimensionsMm(Mm(o.W), Mm(o.H))).ToList(),
            LargestReusableAreaM2 = M2(largestReusable),
            MinReusableDimensionMm = minReusableDim is long d ? Mm(d) : null,
            FragmentCount = fragments,
            ThinFragmentCount = thin,
            FragmentationIndex = residualArea == 0 ? 0 : Math.Round(1m - (decimal)largestResidualLeaf / residualArea, 6),
            ConsumedLengthMm = consumed is long cu ? Mm(cu) : null,
            OrderedLengthMetres = ordered is long ou ? (decimal)ou / Units.MetreUnits(sc) : null,
            RoundingSurplusMm = surplus is long su ? Mm(su) : null,
            TotalCuts = cuts,
            SourcesUsed = fp.Sources.Count,
            WarehouseSourcesUsed = warehouseUsed,
            MaterialUtilization = Math.Round((decimal)requiredArea / (purchasedArea + stockArea), 6),
        };

        string candidateId = "C-" + OptimizerJson.Sha256Hex(canonical.ToString())[..16];
        return new CandidateCutPlan
        {
            CandidateId = candidateId,
            Origin = fp.Origin,
            Metrics = metrics,
            RequiresPurchase = purchase is not null,
            Purchase = purchase,
            Sources = sources,
            Placements = placements,
            CutTrees = trees,
            CutSteps = steps,
            PredictedResiduals = residuals,
        };
    }
}

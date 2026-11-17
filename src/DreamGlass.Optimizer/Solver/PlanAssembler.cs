using System.Text;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

/// <summary>128-bit structural identity of a finished layout (deterministic, so duplicate detection is reproducible).</summary>
internal readonly record struct PlanKey(ulong A, ulong B);

/// <summary>
/// What the search needs to know about a finished layout before it is worth building and validating the full plan:
/// its metrics (exactly the ones the plan will carry), its identity and its source group.
/// </summary>
internal sealed class PlanDigest
{
    public required PlanMetrics Metrics { get; init; }
    public required PlanKey Key { get; init; }

    /// <summary>"warehouse-only" or "roll:{offerId}".</summary>
    public required string Group { get; init; }
}

/// <summary>
/// Converts a <see cref="FinalPlan"/> into the output contract. All metrics are recomputed from the final trees
/// (spec §6.7), never from heuristic counters. <see cref="Digest"/> is the cheap first half (integers only, no
/// strings); <see cref="Assemble(FinalPlan, PlanDigest)"/> builds the full plan for layouts that are worth validating.
/// </summary>
internal static class PlanAssembler
{
    public const string WarehouseGroup = "warehouse-only";

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

    private static void Mix(ref ulong a, ref ulong b, ulong v)
    {
        unchecked
        {
            a = (a ^ v) * 0x9E3779B97F4A7C15UL;
            a ^= a >> 29;
            b = (b + v) * 0xC2B2AE3D27D4EB4FUL;
            b ^= b >> 31;
        }
    }

    /// <summary>
    /// Metrics and identity of a finished layout in one integer pass over its trees. The metrics are the same object
    /// the assembled plan carries, so ranking a digest and ranking the plan can never disagree.
    /// </summary>
    public static PlanDigest Digest(FinalPlan fp)
    {
        var p = fp.P;
        long sc = p.Scale;
        decimal Mm(long u) => Units.ToMm(u, sc);
        decimal M2(long a) => Units.AreaToM2(a, sc);

        long requiredArea = 0, purchasedArea = 0, stockArea = 0, residualArea = 0, reusableArea = 0, warehouseReusable = 0;
        long largestResidualLeaf = 0, largestReusable = 0, rollWidth = 0;
        long? minReusableDim = null, consumed = null, ordered = null;
        int fragments = 0, thin = 0, cuts = 0, warehouseUsed = 0;
        List<(long W, long H)>? offcuts = null;
        decimal cost = 0;
        string? currency = null;
        string group = WarehouseGroup;
        ulong ha = 0x243F6A8885A308D3UL, hb = 0x13198A2E03707344UL;
        var stack = new Stack<Node>();

        foreach (var fs in fp.Sources)
        {
            var spec = fs.Spec;
            long srcArea = spec.W * fs.Length;
            Mix(ref ha, ref hb, (ulong)spec.Index + 1);
            Mix(ref ha, ref hb, (ulong)fs.Length);
            if (spec.IsRoll)
            {
                purchasedArea += srcArea;
                consumed = fs.Consumed;
                ordered = fs.OrderedLength;
                rollWidth = spec.W;
                currency = spec.Offer!.Currency;
                cost = M2(srcArea) * spec.Offer.PricePerSquareMetre;
                group = spec.Group;
            }
            else
            {
                stockArea += srcArea;
                warehouseUsed++;
            }

            stack.Push(fs.Root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                var r = node.R;
                if (node.A is not null)
                {
                    cuts++;
                    Mix(ref ha, ref hb, node.Axis == CutAxis.Vertical ? 1UL : 2UL);
                    Mix(ref ha, ref hb, (ulong)node.Pos);
                    stack.Push(node.B!);
                    stack.Push(node.A);
                }
                else if (node.Piece >= 0)
                {
                    requiredArea += r.Area;
                    // Identical pieces are interchangeable: identity uses the piece type, not the instance.
                    Mix(ref ha, ref hb, 8UL + ((ulong)p.Instances[node.Piece].Type << 1) + (fp.Rotated[node.Piece] ? 1UL : 0UL));
                    Mix(ref ha, ref hb, (ulong)r.X);
                    Mix(ref ha, ref hb, (ulong)r.Y);
                }
                else
                {
                    long a = r.Area;
                    residualArea += a;
                    fragments++;
                    largestResidualLeaf = Math.Max(largestResidualLeaf, a);
                    var (eligible, isThin) = ResidualClassifier.Evaluate(r.W, r.H, p.Policy, sc);
                    if (isThin) thin++;
                    if (eligible)
                    {
                        reusableArea += a;
                        if (!spec.IsRoll) warehouseReusable += a;
                        largestReusable = Math.Max(largestReusable, a);
                        long shortSide = Math.Min(r.W, r.H);
                        minReusableDim = minReusableDim is long m ? Math.Min(m, shortSide) : shortSide;
                        (offcuts ??= []).Add((r.W, r.H));
                    }

                    Mix(ref ha, ref hb, node.Surplus ? 5UL : 4UL);
                }
            }
        }

        if (requiredArea + residualArea != purchasedArea + stockArea)
            throw new InvalidOperationException("Area conservation violated while assembling a plan.");

        offcuts?.Sort((a, b) =>
        {
            int c = (b.W * b.H).CompareTo(a.W * a.H);
            if (c != 0) return c;
            c = b.W.CompareTo(a.W);
            return c != 0 ? c : b.H.CompareTo(a.H);
        });
        long surplus = ordered is long ou && consumed is long cu ? ou - cu : 0;
        var metrics = new PlanMetrics
        {
            RequiredCutAreaM2 = M2(requiredArea),
            PurchasedAreaM2 = M2(purchasedArea),
            NewConsumedAreaM2 = consumed is long c1 ? M2(rollWidth * c1) : 0,
            RoundingSurplusAreaM2 = M2(rollWidth * surplus),
            PurchaseCost = cost,
            Currency = currency,
            ExistingStockAreaConsumedM2 = M2(stockArea),
            ExistingStockUntouchedAreaM2 = M2(p.TotalRemnantArea - stockArea),
            StockDepletionAreaM2 = M2(stockArea - warehouseReusable),
            TotalResidualAreaM2 = M2(residualArea),
            ReusableResidualAreaM2 = M2(reusableArea),
            UnusableScrapAreaM2 = M2(residualArea - reusableArea),
            ReusableOffcutCount = offcuts?.Count ?? 0,
            ReusableOffcuts = offcuts is null ? [] : offcuts.ConvertAll(o => new DimensionsMm(Mm(o.W), Mm(o.H))),
            LargestReusableAreaM2 = M2(largestReusable),
            MinReusableDimensionMm = minReusableDim is long d ? Mm(d) : null,
            FragmentCount = fragments,
            ThinFragmentCount = thin,
            FragmentationIndex = residualArea == 0 ? 0 : Math.Round(1m - (decimal)largestResidualLeaf / residualArea, 6),
            ConsumedLengthMm = consumed is long c2 ? Mm(c2) : null,
            OrderedLengthMetres = ordered is long o2 ? (decimal)o2 / Units.MetreUnits(sc) : null,
            RoundingSurplusMm = ordered is null ? null : Mm(surplus),
            TotalCuts = cuts,
            SourcesUsed = fp.Sources.Count,
            WarehouseSourcesUsed = warehouseUsed,
            MaterialUtilization = Math.Round((decimal)requiredArea / (purchasedArea + stockArea), 6),
        };

        return new PlanDigest { Metrics = metrics, Key = new PlanKey(ha, hb), Group = group };
    }

    public static CandidateCutPlan Assemble(FinalPlan fp) => Assemble(fp, Digest(fp));

    public static CandidateCutPlan Assemble(FinalPlan fp, PlanDigest digest)
    {
        var p = fp.P;
        long sc = p.Scale;
        decimal Mm(long u) => Units.ToMm(u, sc);
        decimal M2(long a) => Units.AreaToM2(a, sc);
        RectMm R(Rect r) => new(Mm(r.X), Mm(r.Y), Mm(r.W), Mm(r.H));

        var sources = new List<PlanSource>(fp.Sources.Count);
        var trees = new List<SourceCutTree>(fp.Sources.Count);
        var steps = new List<CutStep>();
        var residuals = new List<PredictedResidual>();
        var placements = new List<(int Source, Rect R, Placement Dto)>(p.Instances.Length);
        var canonical = new StringBuilder();
        PurchaseLine? purchase = null;

        for (int si = 0; si < fp.Sources.Count; si++)
        {
            var fs = fp.Sources[si];
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
                var offer = spec.Offer!;
                decimal cost = M2(srcArea) * offer.PricePerSquareMetre;
                long paddingLength = PaddingLength(fs.Root, p, fp.Rotated);
                purchase = new PurchaseLine
                {
                    RollOfferId = offer.OfferId,
                    OrderedWidthMm = Mm(spec.W),
                    OrderedLengthMetres = (decimal)fs.Length / spec.MetreUnits,
                    ConsumedLengthMm = Mm(fs.Consumed),
                    PaddingLengthMm = Mm(paddingLength),
                    LengthWithoutPaddingMm = Mm(fs.Consumed - paddingLength),
                    RoundingSurplusMm = Mm(fs.Length - fs.Consumed),
                    PurchasedAreaM2 = M2(srcArea),
                    PricePerSquareMetre = offer.PricePerSquareMetre,
                    PurchaseCost = cost,
                    PurchaseCostRounded = Money.Round(cost),
                    Currency = offer.Currency,
                    FixedLength = spec.FixedLengthUnits is not null,
                };
            }

            var order = Node.PreOrder(fs.Root).ToList();
            var nodes = new List<CutTreeNode>(order.Count);
            var ids = new Dictionary<Node, string>(order.Count, ReferenceEqualityComparer.Instance);
            for (int k = 0; k < order.Count; k++) ids[order[k]] = $"{spec.SourceId}/n{k + 1}";
            int residualSeq = 0;
            foreach (var node in order)
            {
                var r = node.R;
                if (node.A is not null)
                {
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
                    string id = fp.InstanceIds[inst];
                    nodes.Add(new CutTreeNode { NodeId = ids[node], Kind = CutNodeKind.Piece, Region = R(r), PieceInstanceId = id });
                    placements.Add((si, r, new Placement
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
                        RequiredOrientation = t.Required,
                        SourcePatternAxis = spec.Axis,
                    }));
                    canonical.Append('P').Append(id).Append('@').Append(r.X).Append(',').Append(r.Y).Append(rot ? 'r' : 'u').Append(',');
                }
                else
                {
                    residualSeq++;
                    string rid = $"{spec.SourceId}/r{residualSeq}";
                    var cls = ResidualClassifier.Classify(r.W, r.H, p.Policy, sc);
                    nodes.Add(new CutTreeNode { NodeId = ids[node], Kind = CutNodeKind.Residual, Region = R(r), ResidualId = rid });
                    residuals.Add(new PredictedResidual
                    {
                        ResidualId = rid,
                        SourceId = spec.SourceId,
                        Region = R(r),
                        AreaM2 = M2(r.Area),
                        Eligible = cls.Eligible,
                        Reason = cls.Reason,
                        RoundingSurplus = node.Surplus,
                    });
                    canonical.Append('R').Append(',');
                }
            }

            trees.Add(new SourceCutTree { SourceId = spec.SourceId, RootNodeId = ids[fs.Root], Nodes = nodes });
        }

        placements.Sort((a, b) =>
        {
            int c = a.Source.CompareTo(b.Source);
            if (c != 0) return c;
            c = a.R.Y.CompareTo(b.R.Y);
            return c != 0 ? c : a.R.X.CompareTo(b.R.X);
        });

        string candidateId = "C-" + OptimizerJson.Sha256Hex(canonical.ToString())[..16];
        return new CandidateCutPlan
        {
            CandidateId = candidateId,
            SnapshotHash = p.SnapshotHash,
            JobRevisionId = p.Snapshot.JobRevisionId ?? "",
            Origin = fp.Origin,
            Metrics = digest.Metrics,
            RequiresPurchase = purchase is not null,
            Purchase = purchase,
            Sources = sources,
            Placements = placements.ConvertAll(x => x.Dto),
            CutTrees = trees,
            CutSteps = steps,
            PredictedResiduals = residuals,
        };
    }
}

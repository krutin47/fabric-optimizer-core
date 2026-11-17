using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

internal sealed class FinalSource
{
    public required SourceSpec Spec { get; init; }
    public required Node Root { get; init; }
    public required long Length { get; init; }
    public long Consumed { get; init; }
    public long? OrderedLength { get; init; }
}

/// <summary>A complete plan with final per-source trees (clipped roll, rounding surplus, merged residuals).</summary>
internal sealed class FinalPlan
{
    public required NormalizedProblem P { get; init; }
    public required List<FinalSource> Sources { get; init; }
    public required int[] SourceOf { get; init; }
    public required bool[] Rotated { get; init; }
    public required Node[] NodeOf { get; init; }
    public required string[] InstanceIds { get; init; }
    public required string Origin { get; init; }
    public required string Config { get; init; }
}

internal static class Finalizer
{
    /// <summary>
    /// Spec §6 steps 6–7: compress the roll to its consumed length, round the purchase to an allowed length, insert
    /// the full-width surplus as an explicit residual leaf, merge piece-free subtrees. Returns null when the
    /// state is incomplete or the supplier limits cannot cover the consumed length.
    /// </summary>
    public static FinalPlan? Finalize(PackingState st)
    {
        if (!st.IsComplete) return null;
        st.RefreshDirty();
        var p = st.P;
        int n = p.Instances.Length;
        var nodeOf = new Node?[n];
        var sources = new List<FinalSource>();
        var finalIndexOfLayout = new int[st.Layouts.Length];
        Array.Fill(finalIndexOfLayout, -1);

        for (int li = 0; li < st.Layouts.Length; li++)
        {
            var l = st.Layouts[li];
            if (l.PieceCount == 0) continue;
            var root = Node.Clone(l.Root, nodeOf);
            finalIndexOfLayout[li] = sources.Count;
            if (l.IsRoll)
            {
                long consumed = 0;
                foreach (var node in Node.PreOrder(root))
                    if (node.Piece >= 0) consumed = Math.Max(consumed, node.R.Bottom);
                if (NormalizedProblem.OrderedLength(l.Spec, consumed) is not long ordered) return null;
                root = Clip(root, consumed);
                Node.Canonicalize(root);
                ResplitResiduals(root, p);
                if (ordered > consumed)
                {
                    var top = new Node(new Rect(0, 0, l.Spec.W, ordered), null) { Axis = CutAxis.Horizontal, Pos = consumed };
                    top.A = root;
                    root.Parent = top;
                    top.B = new Node(new Rect(0, consumed, l.Spec.W, ordered - consumed), top) { Surplus = true };
                    root = top;
                }

                sources.Add(new FinalSource { Spec = l.Spec, Root = root, Length = ordered, Consumed = consumed, OrderedLength = ordered });
            }
            else
            {
                Node.Canonicalize(root);
                ResplitResiduals(root, p);
                sources.Add(new FinalSource { Spec = l.Spec, Root = root, Length = l.Spec.L });
            }
        }

        var sourceOf = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (nodeOf[i] is null || st.LayoutOf[i] < 0) throw new InvalidOperationException("Complete state has an unplaced instance.");
            sourceOf[i] = finalIndexOfLayout[st.LayoutOf[i]];
        }

        // Identical instances are interchangeable: label them by position (source, y, x) for the workshop.
        var ids = new string[n];
        var byType = new List<int>[p.Types.Length];
        for (int i = 0; i < n; i++) (byType[p.Instances[i].Type] ??= []).Add(i);
        foreach (var t in p.Types)
        {
            var list = byType[t.Index];
            list.Sort((a, b) =>
            {
                int c = sourceOf[a].CompareTo(sourceOf[b]);
                if (c != 0) return c;
                c = nodeOf[a]!.R.Y.CompareTo(nodeOf[b]!.R.Y);
                return c != 0 ? c : nodeOf[a]!.R.X.CompareTo(nodeOf[b]!.R.X);
            });
            for (int k = 0; k < list.Count; k++) ids[list[k]] = $"{t.PieceId}#{k + 1}";
        }

        return new FinalPlan
        {
            P = p,
            Sources = sources,
            SourceOf = sourceOf,
            Rotated = (bool[])st.Rotated.Clone(),
            NodeOf = nodeOf!,
            InstanceIds = ids,
            Origin = st.Origin,
            Config = st.Config,
        };
    }

    /// <summary>
    /// Leftover re-split. Where two leftovers form an "L" around a block of pieces, the L can be cut into two
    /// rectangles in two ways (cut across first, or along first). The search fixed one way when it placed the
    /// piece; once every piece is in place the better way is known. This pass re-cuts each such L so that less
    /// area ends up below the keep-size, and then so that the bigger keepable leftover is as big as possible.
    /// Pieces do not move and the number of cuts does not change, so the plan stays as valid as before (and is
    /// validated as usual).
    /// </summary>
    internal static void ResplitResiduals(Node root, NormalizedProblem p)
    {
        if (p.Policy is null) return; // nothing is keepable, so no way of cutting the L is better
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            var order = Node.PreOrder(root).ToList();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var n = order[i];
                // The L: n is cut one way, its first part is cut the other way, and both second parts are leftovers.
                if (n.A is not { A: not null } first || n.B is not { IsFree: true, Surplus: false } outer) continue;
                if (first.Axis == n.Axis || first.B is not { IsFree: true, Surplus: false } inner) continue;
                var block = first.A!;
                Rect r = n.R, b = block.R;
                Rect altInner, altOuter;
                if (n.Axis == CutAxis.Horizontal)
                {
                    // Now: across at b.Bottom, then along at b.Right. Alternative: along first.
                    altInner = new Rect(r.X, b.Bottom, b.W, r.Bottom - b.Bottom);
                    altOuter = new Rect(b.Right, r.Y, r.Right - b.Right, r.H);
                }
                else
                {
                    altInner = new Rect(b.Right, r.Y, r.Right - b.Right, b.H);
                    altOuter = new Rect(r.X, b.Bottom, r.W, r.Bottom - b.Bottom);
                }

                if (!Better(altInner, altOuter, inner.R, outer.R, p)) continue;
                var newAxis = n.Axis == CutAxis.Horizontal ? CutAxis.Vertical : CutAxis.Horizontal;
                var newFirst = new Node(newAxis == CutAxis.Vertical ? new Rect(r.X, r.Y, b.W, r.H) : new Rect(r.X, r.Y, r.W, b.H), n)
                {
                    Axis = n.Axis,
                    Pos = n.Axis == CutAxis.Horizontal ? b.Bottom : b.Right,
                };
                newFirst.A = block;
                block.Parent = newFirst;
                newFirst.B = new Node(altInner, newFirst);
                n.Axis = newAxis;
                n.Pos = newAxis == CutAxis.Vertical ? b.Right : b.Bottom;
                n.A = newFirst;
                n.B = new Node(altOuter, n);
                changed = true;
            }

            if (!changed) return;
        }
    }

    /// <summary>True when leftovers (a1, a2) are strictly better than (b1, b2): less scrap, then a bigger largest keepable piece.</summary>
    private static bool Better(Rect a1, Rect a2, Rect b1, Rect b2, NormalizedProblem p)
    {
        (long Scrap, long Largest) Score(Rect x, Rect y)
        {
            bool ex = ResidualClassifier.Evaluate(x.W, x.H, p.Policy, p.Scale).Eligible;
            bool ey = ResidualClassifier.Evaluate(y.W, y.H, p.Policy, p.Scale).Eligible;
            return ((ex ? 0 : x.Area) + (ey ? 0 : y.Area), Math.Max(ex ? x.Area : 0, ey ? y.Area : 0));
        }

        var a = Score(a1, a2);
        var b = Score(b1, b2);
        return a.Scrap < b.Scrap || (a.Scrap == b.Scrap && a.Largest > b.Largest);
    }

    /// <summary>Clips a roll tree to <c>y &lt; c</c>. Horizontal cuts at or beyond <paramref name="c"/> collapse onto their top child.</summary>
    internal static Node Clip(Node root, long c)
    {
        static Node Resolve(Node n, Rect region)
        {
            while (n.A is not null && n.Axis == CutAxis.Horizontal && n.Pos >= region.Bottom) n = n.A;
            if (n.Piece >= 0 && n.R != region) throw new InvalidOperationException("Clipping would alter a piece leaf.");
            n.R = region;
            return n;
        }

        var newRoot = Resolve(root, new Rect(root.R.X, root.R.Y, root.R.W, Math.Min(root.R.Bottom, c) - root.R.Y));
        newRoot.Parent = null;
        var stack = new Stack<Node>();
        stack.Push(newRoot);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.A is null) continue;
            var r = n.R;
            Rect ra, rb;
            if (n.Axis == CutAxis.Vertical)
            {
                ra = new Rect(r.X, r.Y, n.Pos - r.X, r.H);
                rb = new Rect(n.Pos, r.Y, r.Right - n.Pos, r.H);
            }
            else
            {
                ra = new Rect(r.X, r.Y, r.W, n.Pos - r.Y);
                rb = new Rect(r.X, n.Pos, r.W, r.Bottom - n.Pos);
            }

            var a = Resolve(n.A, ra);
            var b = Resolve(n.B!, rb);
            n.A = a;
            a.Parent = n;
            n.B = b;
            b.Parent = n;
            stack.Push(b);
            stack.Push(a);
        }

        return newRoot;
    }
}

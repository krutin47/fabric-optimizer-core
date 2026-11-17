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

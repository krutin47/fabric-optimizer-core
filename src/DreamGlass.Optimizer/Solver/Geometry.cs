using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Solver;

/// <summary>Half-open integer rectangle <c>[X, X+W) × [Y, Y+H)</c> in scaled units.</summary>
internal readonly record struct Rect(long X, long Y, long W, long H)
{
    public long Right => X + W;
    public long Bottom => Y + H;
    public long Area => W * H;
}

/// <summary>
/// Binary guillotine cut-tree node. A leaf is either a piece (<see cref="Piece"/> ≥ 0) or a free/residual region.
/// An internal node splits its whole region at <see cref="Pos"/> along <see cref="Axis"/>; child
/// <see cref="A"/> is left/top and <see cref="B"/> is right/bottom. Every placement is created by such splits,
/// so trees are guillotine-legal by construction (and are still re-checked by the independent validator).
/// </summary>
internal sealed class Node(Rect r, Node? parent)
{
    public Rect R = r;
    public Node? Parent = parent;
    public Node? A;
    public Node? B;
    public CutAxis Axis;
    public long Pos;
    public int Piece = -1;

    /// <summary>Marks the whole-metre rounding surplus leaf of a finalised roll.</summary>
    public bool Surplus;

    public bool IsLeaf => A is null;
    public bool IsFree => A is null && Piece < 0;

    public void Split(CutAxis axis, long pos)
    {
        if (!IsFree) throw new InvalidOperationException("Only free leaves can be split.");
        if (axis == CutAxis.Vertical)
        {
            if (pos <= R.X || pos >= R.Right) throw new InvalidOperationException("Cut outside region.");
            A = new Node(new Rect(R.X, R.Y, pos - R.X, R.H), this);
            B = new Node(new Rect(pos, R.Y, R.Right - pos, R.H), this);
        }
        else
        {
            if (pos <= R.Y || pos >= R.Bottom) throw new InvalidOperationException("Cut outside region.");
            A = new Node(new Rect(R.X, R.Y, R.W, pos - R.Y), this);
            B = new Node(new Rect(R.X, pos, R.W, R.Bottom - pos), this);
        }

        Axis = axis;
        Pos = pos;
    }

    /// <summary>
    /// Isolates a <paramref name="w"/> × <paramref name="h"/> piece at the leaf's top-left corner with at most two
    /// full-region cuts and returns the node that will hold it. New residual leaves are appended to <paramref name="newFree"/>.
    /// </summary>
    public static Node CarvePiece(Node leaf, long w, long h, bool horizontalFirst, List<Node>? newFree)
    {
        var r = leaf.R;
        if (w > r.W || h > r.H) throw new InvalidOperationException("Piece does not fit the leaf.");
        if (w == r.W && h == r.H) return leaf;
        if (w == r.W)
        {
            leaf.Split(CutAxis.Horizontal, r.Y + h);
            newFree?.Add(leaf.B!);
            return leaf.A!;
        }

        if (h == r.H)
        {
            leaf.Split(CutAxis.Vertical, r.X + w);
            newFree?.Add(leaf.B!);
            return leaf.A!;
        }

        if (horizontalFirst)
        {
            leaf.Split(CutAxis.Horizontal, r.Y + h);
            var top = leaf.A!;
            top.Split(CutAxis.Vertical, r.X + w);
            newFree?.Add(top.B!);
            newFree?.Add(leaf.B!);
            return top.A!;
        }
        else
        {
            leaf.Split(CutAxis.Vertical, r.X + w);
            var left = leaf.A!;
            left.Split(CutAxis.Horizontal, r.Y + h);
            newFree?.Add(left.B!);
            newFree?.Add(leaf.B!);
            return left.A!;
        }
    }

    /// <summary>Iterative deep clone (trees can be thousands deep). Fills <paramref name="pieceMap"/>[piece] with clones.</summary>
    public static Node Clone(Node root, Node?[]? pieceMap)
    {
        var copy = Shallow(root, null);
        var stack = new Stack<(Node Src, Node Dst)>();
        stack.Push((root, copy));
        while (stack.Count > 0)
        {
            var (s, d) = stack.Pop();
            if (s.A is not null)
            {
                d.A = Shallow(s.A, d);
                d.B = Shallow(s.B!, d);
                stack.Push((s.B!, d.B));
                stack.Push((s.A, d.A));
            }
            else if (s.Piece >= 0 && pieceMap is not null)
            {
                pieceMap[s.Piece] = d;
            }
        }

        return copy;
    }

    private static Node Shallow(Node s, Node? parent) =>
        new(s.R, parent) { Axis = s.Axis, Pos = s.Pos, Piece = s.Piece, Surplus = s.Surplus };

    /// <summary>Iterative pre-order traversal (parent, then A subtree, then B subtree).</summary>
    public static IEnumerable<Node> PreOrder(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n.A is not null)
            {
                stack.Push(n.B!);
                stack.Push(n.A);
            }
        }
    }

    /// <summary>Merges every subtree that contains no piece into a single residual leaf (removes needless cuts).</summary>
    public static void Canonicalize(Node root)
    {
        var order = PreOrder(root).ToList();
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            if (n.A is not null && n.A.IsFree && n.B!.IsFree && !n.A.Surplus && !n.B.Surplus)
            {
                n.A = null;
                n.B = null;
            }
        }
    }
}

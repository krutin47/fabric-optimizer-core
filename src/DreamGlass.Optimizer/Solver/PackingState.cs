using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

/// <summary>Working cut tree of one physical source during search.</summary>
internal sealed class SourceLayout
{
    public required SourceSpec Spec { get; init; }
    public required Node Root { get; set; }

    /// <summary>Canonical length of the working region (remnant length, or the trial bound of a fresh roll).</summary>
    public required long Bound { get; init; }

    public int PieceCount { get; set; }
    public long Consumed { get; set; }

    /// <summary>
    /// Free leaves that can still take a piece. Leaves too small for every piece of the job are left out, so
    /// placement scans stay short; their area is counted in <see cref="DeadArea"/>.
    /// </summary>
    public List<Node> Free { get; } = [];

    /// <summary>Area of free leaves that are too small for any piece of this job.</summary>
    public long DeadArea { get; set; }

    public required long MinPieceSide { get; init; }
    public required long MinPieceArea { get; init; }

    /// <summary>True when no piece of the job can ever fit this free region.</summary>
    public bool IsDead(Rect r) => Math.Min(r.W, r.H) < MinPieceSide || r.W * r.H < MinPieceArea;

    /// <summary>Drops leaves added to <see cref="Free"/> from index <paramref name="from"/> on that no piece can use.</summary>
    public void DropDeadFrom(int from)
    {
        for (int i = Free.Count - 1; i >= from; i--)
        {
            if (!IsDead(Free[i].R)) continue;
            DeadArea += Free[i].R.Area;
            Free.RemoveAt(i);
        }
    }
    public bool Enabled { get; set; } = true;
    public bool Dirty { get; set; }

    public bool IsRoll => Spec.IsRoll;

    /// <summary>
    /// The roll "frontier": the full-width free leaf that reaches the trial bound. Placements into it always cut
    /// full width first, which guarantees every remaining piece can still be stacked (construction never fails
    /// on a roll for lack of a full-width region).
    /// </summary>
    public bool IsFrontier(Node n) => IsRoll && n.R.X == 0 && n.R.W == Spec.W && n.R.Bottom == Bound;

    public static SourceLayout Create(SourceSpec spec, long bound, NormalizedProblem p)
    {
        var l = new SourceLayout
        {
            Spec = spec,
            Bound = bound,
            Root = new Node(new Rect(0, 0, spec.W, bound), null),
            MinPieceSide = p.MinPieceSide,
            MinPieceArea = p.MinPieceArea,
        };
        l.Free.Add(l.Root);
        return l;
    }

    public SourceLayout CloneInto(Node?[] pieceMap)
    {
        var c = new SourceLayout
        {
            Spec = Spec,
            Bound = Bound,
            Root = Node.Clone(Root, pieceMap),
            Enabled = Enabled,
            MinPieceSide = MinPieceSide,
            MinPieceArea = MinPieceArea,
        };
        c.RebuildFree();
        return c;
    }

    public void RebuildFree()
    {
        Free.Clear();
        PieceCount = 0;
        Consumed = 0;
        DeadArea = 0;
        foreach (var n in Node.PreOrder(Root))
        {
            if (!n.IsLeaf) continue;
            if (n.Piece >= 0)
            {
                PieceCount++;
                Consumed = Math.Max(Consumed, n.R.Bottom);
            }
            else if (IsDead(n.R))
            {
                DeadArea += n.R.Area;
            }
            else
            {
                Free.Add(n);
            }
        }

        Dirty = false;
    }
}

/// <summary>A (possibly partial) assignment of piece instances to guillotine trees over a set of sources.</summary>
internal sealed class PackingState
{
    public required NormalizedProblem P { get; init; }
    public required SourceLayout[] Layouts { get; init; }
    public required int[] LayoutOf { get; init; }
    public required bool[] Rotated { get; init; }
    public required Node?[] NodeOf { get; init; }
    public required string Config { get; init; }
    public int Placed { get; set; }
    public string Origin { get; set; } = "";
    public long Hash { get; set; }

    public int RollLayout
    {
        get
        {
            for (int i = 0; i < Layouts.Length; i++)
                if (Layouts[i].IsRoll) return i;
            return -1;
        }
    }

    public static PackingState Create(NormalizedProblem p, IReadOnlyList<int> sourceIndices, string config)
    {
        var layouts = new SourceLayout[sourceIndices.Count];
        for (int i = 0; i < layouts.Length; i++)
        {
            var spec = p.Sources[sourceIndices[i]];
            layouts[i] = SourceLayout.Create(spec, spec.IsRoll ? p.RollTrialLength(spec) : spec.L, p);
        }

        int n = p.Instances.Length;
        var layoutOf = new int[n];
        Array.Fill(layoutOf, -1);
        return new PackingState
        {
            P = p,
            Layouts = layouts,
            LayoutOf = layoutOf,
            Rotated = new bool[n],
            NodeOf = new Node?[n],
            Config = config,
        };
    }

    public PackingState Clone()
    {
        var nodeOf = new Node?[NodeOf.Length];
        var layouts = new SourceLayout[Layouts.Length];
        for (int i = 0; i < layouts.Length; i++) layouts[i] = Layouts[i].CloneInto(nodeOf);
        return new PackingState
        {
            P = P,
            Layouts = layouts,
            LayoutOf = (int[])LayoutOf.Clone(),
            Rotated = (bool[])Rotated.Clone(),
            NodeOf = nodeOf,
            Config = Config,
            Placed = Placed,
            Origin = Origin,
            Hash = Hash,
        };
    }

    public bool IsComplete => Placed == P.Instances.Length;

    /// <remarks>
    /// <paramref name="section"/> only matters for a piece that opens a new section on the roll frontier: it is the
    /// section's length along the roll. 0 (or the piece's own length) cuts the section as long as the piece. A longer
    /// section is cut off the frontier first and the piece is carved along the section's full length, leaving the rest
    /// of the row beside it and a full-length layer below.
    /// </remarks>
    public void Place(int layout, Node leaf, int instance, Orientation o, bool horizontalFirst, long section = 0)
    {
        var l = Layouts[layout];
        int idx = l.Free.IndexOf(leaf);
        if (idx < 0) throw new InvalidOperationException("Leaf is not free in this layout.");
        l.Free[idx] = l.Free[^1];
        l.Free.RemoveAt(l.Free.Count - 1);
        int firstNew = l.Free.Count;

        bool hf = horizontalFirst || l.IsFrontier(leaf);
        if (l.IsFrontier(leaf) && section > o.H && section <= leaf.R.H)
        {
            if (section < leaf.R.H)
            {
                leaf.Split(CutAxis.Horizontal, leaf.R.Y + section);
                l.Free.Add(leaf.B!);
                leaf = leaf.A!;
            }

            hf = false;
        }

        var pn = Node.CarvePiece(leaf, o.W, o.H, hf, l.Free);
        l.DropDeadFrom(firstNew);
        Attach(layout, pn, instance, o.Rotated);
        unchecked
        {
            Hash = (Hash * 1_000_003) ^ ((layout + 1) * 7919L) ^ (pn.R.X * 31 + pn.R.Y * 131 + o.W * 17 + o.H) ^ (section * 7);
        }
    }

    /// <summary>Marks an already carved leaf as holding <paramref name="instance"/> (used by the exact solver).</summary>
    public void Attach(int layout, Node pieceNode, int instance, bool rotated)
    {
        var l = Layouts[layout];
        pieceNode.Piece = instance;
        NodeOf[instance] = pieceNode;
        LayoutOf[instance] = layout;
        Rotated[instance] = rotated;
        Placed++;
        l.PieceCount++;
        l.Consumed = Math.Max(l.Consumed, pieceNode.R.Bottom);
    }

    /// <summary>Removes a placed instance; its leaf becomes free and empty sibling subtrees are merged upward.</summary>
    public void Remove(int instance)
    {
        var n = NodeOf[instance] ?? throw new InvalidOperationException("Instance is not placed.");
        var l = Layouts[LayoutOf[instance]];
        n.Piece = -1;
        NodeOf[instance] = null;
        LayoutOf[instance] = -1;
        Placed--;
        var cur = n.Parent;
        while (cur is not null && cur.A!.IsFree && cur.B!.IsFree)
        {
            cur.A = null;
            cur.B = null;
            cur = cur.Parent;
        }

        l.Dirty = true;
    }

    public void RefreshDirty()
    {
        foreach (var l in Layouts)
            if (l.Dirty) l.RebuildFree();
    }

    public IEnumerable<int> InstancesIn(int layout)
    {
        for (int i = 0; i < LayoutOf.Length; i++)
            if (LayoutOf[i] == layout) yield return i;
    }
}

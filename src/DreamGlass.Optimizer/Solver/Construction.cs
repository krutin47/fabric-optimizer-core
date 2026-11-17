using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

/// <summary>Guillotine free-region construction: greedy best placement and a bounded beam search over placements.</summary>
internal static class Construction
{
    internal readonly record struct Move(int Layout, Node Leaf, Orientation O, Score S);

    public static Score Evaluate(PackingState st, int layout, Node leaf, Orientation o, PlacementPolicy pol, long maxRemnantArea, long tie)
    {
        var l = st.Layouts[layout];
        var r = leaf.R;
        long ext = l.IsRoll ? Math.Max(0, r.Y + o.H - l.Consumed) : 0;
        long open = 0;
        if (!l.IsRoll && l.PieceCount == 0)
        {
            open = pol.Open switch
            {
                OpenPreference.SmallestFirst => 1 + l.Spec.Area,
                OpenPreference.LargestFirst => 1 + maxRemnantArea - l.Spec.Area,
                _ => 1,
            };
        }

        long isRoll = l.IsRoll ? 1 : 0;
        long dw = r.W - o.W, dh = r.H - o.H;
        (long f1, long f2) = pol.Fit switch
        {
            FitRule.BestAreaFit => (r.Area - o.W * o.H, Math.Min(dw, dh)),
            FitRule.BestShortSideFit => (Math.Min(dw, dh), Math.Max(dw, dh)),
            FitRule.BestLongSideFit => (Math.Max(dw, dh), Math.Min(dw, dh)),
            _ => (r.Y + o.H, r.X),
        };

        return pol.Tiers switch
        {
            TierOrder.MinExtension => new Score(ext, open, f1, f2, tie, 0),
            TierOrder.RemnantsFirst => new Score(isRoll, ext, open, f1, f2, tie),
            TierOrder.OpenedFirst => new Score(open, ext, f1, f2, tie, 0),
            _ => new Score(f1, ext, open, f2, tie, 0),
        };
    }

    public static long MaxRemnantArea(PackingState st)
    {
        long m = 0;
        foreach (var l in st.Layouts)
            if (!l.IsRoll) m = Math.Max(m, l.Spec.Area);
        return m;
    }

    /// <summary>Greedily places every instance in <paramref name="order"/>; false if one cannot be placed.</summary>
    public static bool PlaceAll(PackingState st, IReadOnlyList<int> order, PlacementPolicy pol, Rng rng)
    {
        long maxArea = MaxRemnantArea(st);
        foreach (int inst in order)
        {
            if (st.NodeOf[inst] is not null) continue;
            if (!TryPlaceBest(st, inst, pol, rng, maxArea)) return false;
        }

        return true;
    }

    public static bool TryPlaceBest(PackingState st, int inst, PlacementPolicy pol, Rng rng, long maxRemnantArea)
    {
        var best = Score.Max;
        Move? move = null;
        var type = st.P.TypeOf(inst);
        for (int li = 0; li < st.Layouts.Length; li++)
        {
            var l = st.Layouts[li];
            if (!l.Enabled) continue;
            var oris = type.For(l.Spec.Axis);
            var free = l.Free;
            for (int fi = 0; fi < free.Count; fi++)
            {
                var leaf = free[fi];
                foreach (var o in oris)
                {
                    if (o.W > leaf.R.W || o.H > leaf.R.H) continue;
                    long tie = pol.RandomTies ? rng.NextLong() : 0;
                    var s = Evaluate(st, li, leaf, o, pol, maxRemnantArea, tie);
                    if (s < best)
                    {
                        best = s;
                        move = new Move(li, leaf, o, s);
                    }
                }
            }
        }

        if (move is not { } m) return false;
        long section = pol.Rows && st.Layouts[m.Layout].IsFrontier(m.Leaf) ? ChooseSection(st, m.Layout, m.Leaf, inst, m.O) : 0;
        st.Place(m.Layout, m.Leaf, inst, m.O, pol.HorizontalFirst(m.Leaf.R, m.O.W, m.O.H), section);
        return true;
    }

    /// <summary>Most candidate section lengths tried per new section (shortest first).</summary>
    private const int MaxSectionCandidates = 32;

    /// <summary>
    /// Row sections. A piece that opens a new section of the roll normally makes the section exactly as long as
    /// itself, so pieces stacked across the roll beside it must fit within that length. A section can be better
    /// as long as a row of pending pieces side by side along the roll: three 510 mm panels make a 1,530 mm section
    /// with a 1,440 mm panel under them, where a 1,440 mm section only takes two of them.
    /// <para>
    /// Candidate lengths are the opening piece's own length and every row of 1..n copies of a pending piece type
    /// (each legal orientation), from the opening length up to twice the longest pending piece. Each candidate is
    /// packed on a scratch section (opening piece in its corner, then the pending pieces largest first, best area
    /// fit) and the best-filled section wins; ties keep the shorter one. Only the section length is decided here:
    /// the real placements that follow still go through the policy, and the finalised plan is validated as usual.
    /// </para>
    /// </summary>
    internal static long ChooseSection(PackingState st, int layout, Node leaf, int inst, Orientation o)
    {
        var p = st.P;
        var spec = st.Layouts[layout].Spec;
        long w = spec.W, room = leaf.R.H;
        var pending = new List<int>();
        for (int i = 0; i < p.Instances.Length; i++)
            if (i != inst && st.NodeOf[i] is null) pending.Add(i);
        if (pending.Count == 0) return o.H;
        pending.Sort((a, b) =>
        {
            int c = p.TypeOf(b).FootArea.CompareTo(p.TypeOf(a).FootArea);
            return c != 0 ? c : a.CompareTo(b);
        });

        // Rows: n copies of one type side by side along the roll (the opening piece counts for its own type).
        long longest = o.H;
        var copies = new Dictionary<int, int> { [p.Instances[inst].Type] = 1 };
        foreach (int i in pending)
        {
            int ty = p.Instances[i].Type;
            copies[ty] = copies.GetValueOrDefault(ty) + 1;
            foreach (var po in p.Types[ty].For(spec.Axis))
                if (po.W <= w) longest = Math.Max(longest, po.H);
        }

        long cap = Math.Min(room, 2 * longest);
        var lengths = new SortedSet<long>();
        foreach (var (ty, n) in copies.OrderBy(kv => kv.Key))
            foreach (var po in p.Types[ty].For(spec.Axis))
            {
                if (po.W > w) continue;
                for (int k = 1; k <= n; k++)
                {
                    long len = k * po.H;
                    if (len > cap) break;
                    if (len > o.H) lengths.Add(len);
                }
            }

        long best = o.H;
        long bestFill = SectionFill(p, spec, o, o.H, pending);
        int tried = 0;
        foreach (long len in lengths)
        {
            if (++tried > MaxSectionCandidates) break;
            long fill = SectionFill(p, spec, o, len, pending);
            // fill / (w × len) > bestFill / (w × best), compared without division.
            if ((Int128)fill * best > (Int128)bestFill * len)
            {
                best = len;
                bestFill = fill;
            }
        }

        return best;
    }

    /// <summary>Piece area a scratch section of <paramref name="len"/> takes: opening piece first, then pending pieces greedily.</summary>
    private static long SectionFill(NormalizedProblem p, SourceSpec spec, Orientation o, long len, List<int> pending)
    {
        var root = new Node(new Rect(0, 0, spec.W, len), null);
        var free = new List<Node>();
        Node.CarvePiece(root, o.W, o.H, horizontalFirst: false, free);
        long filled = o.W * o.H;
        foreach (int i in pending)
        {
            Node? bestLeaf = null;
            Orientation bestO = default;
            long bestWaste = long.MaxValue;
            foreach (var leaf in free)
                foreach (var po in p.TypeOf(i).For(spec.Axis))
                {
                    if (po.W > leaf.R.W || po.H > leaf.R.H) continue;
                    long waste = leaf.R.Area - po.W * po.H;
                    if (waste < bestWaste)
                    {
                        bestWaste = waste;
                        bestLeaf = leaf;
                        bestO = po;
                    }
                }

            if (bestLeaf is null) continue;
            free.Remove(bestLeaf);
            long dw = bestLeaf.R.W - bestO.W, dh = bestLeaf.R.H - bestO.H;
            // Keep the bigger leftover whole (same rule as SplitRule.MaximizeLargerResidual).
            bool hf = Math.Max(dw * bestO.H, bestLeaf.R.W * dh) >= Math.Max(dw * bestLeaf.R.H, bestO.W * dh);
            Node.CarvePiece(bestLeaf, bestO.W, bestO.H, hf, free);
            filled += bestO.W * bestO.H;
        }

        return filled;
    }

    private static List<Move> TopMoves(PackingState st, int inst, PlacementPolicy pol, int k, Rng rng, long maxRemnantArea)
    {
        var all = new List<Move>();
        var type = st.P.TypeOf(inst);
        for (int li = 0; li < st.Layouts.Length; li++)
        {
            var l = st.Layouts[li];
            if (!l.Enabled) continue;
            foreach (var leaf in l.Free)
                foreach (var o in type.For(l.Spec.Axis))
                {
                    if (o.W > leaf.R.W || o.H > leaf.R.H) continue;
                    long tie = pol.RandomTies ? rng.NextLong() : 0;
                    all.Add(new Move(li, leaf, o, Evaluate(st, li, leaf, o, pol, maxRemnantArea, tie)));
                }
        }

        all.Sort((a, b) => a.S.CompareTo(b.S));
        if (all.Count > k) all.RemoveRange(k, all.Count - k);
        return all;
    }

    /// <summary>
    /// Beam search over placements: at each step every beam state proposes its best <paramref name="branching"/>
    /// moves; children are ranked by a partial-plan estimate (roll length, dead area, committed stock) and the best
    /// <paramref name="width"/> distinct states survive. Only surviving children are cloned.
    /// </summary>
    public static PackingState? Beam(PackingState start, IReadOnlyList<int> order, PlacementPolicy pol, int width, int branching, Rng rng, SearchBudget budget)
    {
        var p = start.P;
        int n = order.Count;
        var minArea = new long[n + 1];
        var minSide = new long[n + 1];
        minArea[n] = long.MaxValue;
        minSide[n] = long.MaxValue;
        for (int i = n - 1; i >= 0; i--)
        {
            var t = p.TypeOf(order[i]);
            minArea[i] = Math.Min(minArea[i + 1], t.FootArea);
            minSide[i] = Math.Min(minSide[i + 1], t.MinSide);
        }

        long maxRemnantArea = MaxRemnantArea(start);
        var beam = new List<PackingState> { start };
        for (int k = 0; k < n; k++)
        {
            if (budget.Expired) return null;
            int inst = order[k];
            var proposals = new List<(int Parent, Move M, Score Est)>();
            for (int bi = 0; bi < beam.Count; bi++)
            {
                var s = beam[bi];
                (long roll, long dead, long opened) = Summary(s, minArea[k + 1], minSide[k + 1]);
                foreach (var mv in TopMoves(s, inst, pol, branching, rng, maxRemnantArea))
                {
                    var l = s.Layouts[mv.Layout];
                    long roll2 = l.IsRoll ? Math.Max(roll, mv.Leaf.R.Y + mv.O.H) : roll;
                    long opened2 = !l.IsRoll && l.PieceCount == 0 ? opened + l.Spec.Area : opened;
                    long dead2 = dead + MoveDeadArea(s, mv, pol, minArea[k + 1], minSide[k + 1]);
                    proposals.Add((bi, mv, new Score(roll2, dead2, opened2, mv.S.A, mv.S.B, mv.S.C)));
                }
            }

            if (proposals.Count == 0) return null;
            proposals.Sort((a, b) =>
            {
                int c = a.Est.CompareTo(b.Est);
                return c != 0 ? c : a.Parent.CompareTo(b.Parent);
            });

            var next = new List<PackingState>(width);
            var seen = new HashSet<long>();
            foreach (var (parent, mv, _) in proposals)
            {
                if (next.Count >= width) break;
                var c = beam[parent].Clone();
                var leaf = c.Layouts[mv.Layout].Free.First(f => f.R == mv.Leaf.R);
                c.Place(mv.Layout, leaf, inst, mv.O, pol.HorizontalFirst(leaf.R, mv.O.W, mv.O.H));
                if (seen.Add(c.Hash)) next.Add(c);
            }

            beam = next;
        }

        return beam[0];
    }

    private static bool IsDead(Rect r, long minArea, long minSide) =>
        r.W * r.H < minArea || Math.Min(r.W, r.H) < minSide;

    private static (long Roll, long Dead, long Opened) Summary(PackingState s, long minArea, long minSide)
    {
        long roll = 0, dead = 0, opened = 0;
        foreach (var l in s.Layouts)
        {
            if (l.IsRoll) roll = l.Consumed;
            if (l.PieceCount == 0) continue;
            if (!l.IsRoll) opened += l.Spec.Area;
            foreach (var f in l.Free)
                if (!l.IsFrontier(f) && IsDead(f.R, minArea, minSide))
                    dead += f.R.Area;
        }

        return (roll, dead, opened);
    }

    /// <summary>Change in dead area if <paramref name="mv"/> is applied (the consumed leaf was live because the piece fits it).</summary>
    private static long MoveDeadArea(PackingState s, Move mv, PlacementPolicy pol, long minArea, long minSide)
    {
        var l = s.Layouts[mv.Layout];
        var r = mv.Leaf.R;
        long w = mv.O.W, h = mv.O.H;
        bool frontier = l.IsFrontier(mv.Leaf);
        bool hf = frontier || pol.HorizontalFirst(r, w, h);
        long dead = 0;
        void Add(long rw, long rh, bool isFrontier)
        {
            if (rw <= 0 || rh <= 0 || isFrontier) return;
            if (rw * rh < minArea || Math.Min(rw, rh) < minSide) dead += rw * rh;
        }

        if (hf)
        {
            Add(r.W - w, h, false);
            Add(r.W, r.H - h, frontier);
        }
        else
        {
            Add(r.W - w, r.H, false);
            Add(w, r.H - h, false);
        }

        // Opening a remnant turns its other free leaves (none yet beyond the root) into accounted space.
        return dead;
    }
}

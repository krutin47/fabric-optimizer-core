using System.Diagnostics;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Solver;

namespace DreamGlass.Optimizer.Exact;

internal sealed class ExactOutcome
{
    public bool Attempted { get; init; }
    public bool Completed { get; set; }
    public string Reason { get; set; } = "";
    public long States { get; set; }
    public long ElapsedMs { get; set; }
    public long? MinPurchasedAreaUnits { get; set; }
    public bool ProvenInfeasible { get; set; }
    public PackingState? Plan { get; set; }
}

/// <summary>
/// Exact small-case benchmark (spec §6): stage-unrestricted guillotine packing with 0°/90° rotation under the
/// grain rules. <c>MinLen(g, w, S)</c> is the minimum length needed to pack piece multiset <c>S</c> in a strip of
/// width <c>w</c> on a source of grain class <c>g</c>; it is computed by memoised recursion over the first cut
/// (horizontal: split S, add lengths; vertical: split S and width, take the max). Cut positions are restricted
/// to normal patterns (sums of piece widths), which is complete for guillotine packings, and widths are floored
/// to the multiset's normal set to share memo entries. On top, all assignments of pieces to remnants are
/// enumerated and the remainder goes to the cheapest-area roll offer. A completed run therefore <b>proves</b> the
/// minimum newly purchased area (tier 1 of the objective) — and only that.
/// </summary>
internal sealed class ExactGuillotineSolver
{
    private sealed class AbortException(string reason) : Exception(reason);

    private struct Entry
    {
        public long Len;
        public byte Kind;
        public int Sub;
        public long P;
        public int Ori;
    }

    private const byte KBase = 1, KHorizontal = 2, KVertical = 3;
    private const long Inf = long.MaxValue / 4;

    private readonly NormalizedProblem _p;
    private readonly ExactSolverOptions _o;
    private readonly SearchBudget _budget;
    private readonly long _deadlineMs;
    private readonly int _t;
    private readonly int[] _q;
    private readonly int[] _stride;
    private readonly int _total;
    private readonly Orientation[][][] _ori;
    private readonly int[] _count;
    private readonly long[] _area;
    private readonly int[] _single;
    private readonly int[]?[] _subs;
    private readonly Dictionary<(int G, int S, long W), Entry> _memo = [];
    private readonly Dictionary<(int G, int S), long[]> _normal = [];
    private readonly long _maxW;
    private long _states;

    private ExactGuillotineSolver(NormalizedProblem p, ExactSolverOptions o, SearchBudget budget, long deadlineMs, int total)
    {
        _p = p;
        _o = o;
        _budget = budget;
        _deadlineMs = deadlineMs;
        _t = p.Types.Length;
        _q = p.Types.Select(t => t.Quantity).ToArray();
        _stride = new int[_t];
        int s = 1;
        for (int i = 0; i < _t; i++)
        {
            _stride[i] = s;
            s *= _q[i] + 1;
        }

        _total = total;
        _ori = [p.Types.Select(t => t.OnLengthAxis).ToArray(), p.Types.Select(t => t.OnWidthAxis).ToArray()];
        _count = new int[total];
        _area = new long[total];
        _single = new int[total];
        _subs = new int[]?[total];
        for (int idx = 0; idx < total; idx++)
        {
            int n = 0, single = -1;
            long a = 0;
            for (int t = 0; t < _t; t++)
            {
                int c = Count(idx, t);
                n += c;
                a += c * p.Types[t].FootArea;
                if (c > 0) single = t;
            }

            _count[idx] = n;
            _area[idx] = a;
            _single[idx] = single;
        }

        _maxW = p.Sources.Length == 0 ? 0 : p.Sources.Max(x => x.W);
    }

    public static ExactOutcome TrySolve(NormalizedProblem p, ExactSolverOptions o, SearchBudget budget, long deadlineMs)
    {
        if (!o.Enabled) return new ExactOutcome { Reason = "Exact solver disabled." };
        if (p.Instances.Length > o.MaxInstances)
            return new ExactOutcome { Reason = $"{p.Instances.Length} instances exceed the exact limit of {o.MaxInstances}." };
        if (p.RemnantSources.Length > o.MaxRemnants)
            return new ExactOutcome { Reason = $"{p.RemnantSources.Length} remnants exceed the exact limit of {o.MaxRemnants}." };
        long total = 1;
        foreach (var t in p.Types)
        {
            total *= t.Quantity + 1;
            if (total > o.MaxMultisets)
                return new ExactOutcome { Reason = $"Piece multiset space exceeds the exact limit of {o.MaxMultisets}." };
        }

        return new ExactGuillotineSolver(p, o, budget, deadlineMs, (int)total).Solve();
    }

    private int Count(int idx, int t) => idx / _stride[t] % (_q[t] + 1);

    private static int G(PatternAxis axis) => axis == PatternAxis.Length ? 0 : 1;

    private void Tick()
    {
        if (++_states > _o.MaxStates) throw new AbortException($"State cap of {_o.MaxStates} reached; no optimality claim.");
        if ((_states & 1023) == 0 && (_budget.Cancelled || _budget.ElapsedMs >= _deadlineMs))
            throw new AbortException(_budget.Cancelled ? "Cancelled." : "Exact time slice exhausted; no optimality claim.");
    }

    /// <summary>All non-empty proper sub-multisets of <paramref name="s"/>, as indices.</summary>
    private int[] Subs(int s)
    {
        if (_subs[s] is { } cached) return cached;
        var counts = new int[_t];
        for (int t = 0; t < _t; t++) counts[t] = Count(s, t);
        var digits = new int[_t];
        var list = new List<int>();
        int idx = 0;
        while (true)
        {
            int t = 0;
            while (t < _t)
            {
                if (digits[t] < counts[t])
                {
                    digits[t]++;
                    idx += _stride[t];
                    break;
                }

                idx -= digits[t] * _stride[t];
                digits[t] = 0;
                t++;
            }

            if (t == _t || idx == s) break;
            list.Add(idx);
        }

        return _subs[s] = list.ToArray();
    }

    /// <summary>Sorted normal widths of <paramref name="s"/> (all sums of chosen piece widths, including 0), capped at the widest source.</summary>
    private long[] Normal(int g, int s)
    {
        if (_normal.TryGetValue((g, s), out var cached)) return cached;
        long[] result;
        if (s == 0)
        {
            result = [0];
        }
        else
        {
            int t = 0;
            while (Count(s, t) == 0) t++;
            var prev = Normal(g, s - _stride[t]);
            var set = new SortedSet<long>(prev);
            foreach (long x in prev)
                foreach (var o in _ori[g][t])
                    if (x + o.W <= _maxW) set.Add(x + o.W);
            result = set.ToArray();
        }

        _normal[(g, s)] = result;
        return result;
    }

    private static long Floor(long[] sorted, long w)
    {
        int i = Array.BinarySearch(sorted, w);
        if (i >= 0) return sorted[i];
        i = ~i - 1;
        return i >= 0 ? sorted[i] : 0;
    }

    /// <summary>Minimum width any packing of <paramref name="s"/> needs (its widest piece in its narrowest legal orientation).</summary>
    private long MinWidthNeeded(int g, int s)
    {
        long m = 0;
        for (int t = 0; t < _t; t++)
            if (Count(s, t) > 0) m = Math.Max(m, _ori[g][t].Min(o => o.W));
        return m;
    }

    private long LowerBound(int g, long w, int s)
    {
        long lb = (_area[s] + w - 1) / w;
        for (int t = 0; t < _t; t++)
        {
            if (Count(s, t) == 0) continue;
            long best = Inf;
            foreach (var o in _ori[g][t])
                if (o.W <= w) best = Math.Min(best, o.H);
            if (best >= Inf) return Inf;
            lb = Math.Max(lb, best);
        }

        return lb;
    }

    private long MinLen(int g, long w, int s)
    {
        if (s == 0) return 0;
        long wn = Floor(Normal(g, s), w);
        if (wn == 0) return Inf;
        if (_memo.TryGetValue((g, s, wn), out var hit)) return hit.Len;
        Tick();
        var e = new Entry { Len = Inf };
        long lb = LowerBound(g, wn, s);
        if (lb < Inf)
        {
            if (_count[s] == 1)
            {
                var oris = _ori[g][_single[s]];
                for (int i = 0; i < oris.Length; i++)
                    if (oris[i].W <= wn && oris[i].H < e.Len)
                        e = new Entry { Len = oris[i].H, Kind = KBase, Ori = i };
            }
            else
            {
                foreach (int sub in Subs(s))
                {
                    int comp = s - sub;
                    if (sub <= comp)
                    {
                        long a = MinLen(g, wn, sub);
                        if (a < e.Len)
                        {
                            long b = MinLen(g, wn, comp);
                            if (a + b < e.Len) e = new Entry { Len = a + b, Kind = KHorizontal, Sub = sub };
                        }
                    }

                    if (e.Len <= lb) break;
                    long maxLeft = wn - MinWidthNeeded(g, comp);
                    var widths = Normal(g, sub);
                    for (int i = 1; i < widths.Length && widths[i] <= maxLeft; i++)
                    {
                        long pw = widths[i];
                        long a = MinLen(g, pw, sub);
                        if (a >= e.Len) continue;
                        long b = MinLen(g, wn - pw, comp);
                        long m = Math.Max(a, b);
                        if (m < e.Len) e = new Entry { Len = m, Kind = KVertical, Sub = sub, P = pw };
                    }

                    if (e.Len <= lb) break;
                }
            }
        }

        _memo[(g, s, wn)] = e;
        return e.Len;
    }

    private ExactOutcome Solve()
    {
        var sw = Stopwatch.StartNew();
        var outcome = new ExactOutcome { Attempted = true };
        try
        {
            int full = _total - 1;
            var rem = _p.RemnantSources;
            int k = rem.Length;
            var reach = new Dictionary<int, (int Prev, int Sub)>[k + 1];
            reach[0] = new() { [0] = (-1, 0) };
            for (int r = 0; r < k; r++)
            {
                var spec = _p.Sources[rem[r]];
                int g = G(spec.Axis);
                var next = new Dictionary<int, (int Prev, int Sub)>();
                foreach (int idx in reach[r].Keys) next.TryAdd(idx, (idx, 0));
                foreach (int idx in reach[r].Keys)
                {
                    int rest = full - idx;
                    if (rest == 0) continue;
                    foreach (int sub in Subs(rest).Append(rest))
                    {
                        if (next.ContainsKey(idx + sub) || _area[sub] > spec.Area) continue;
                        if (MinLen(g, spec.W, sub) <= spec.L) next[idx + sub] = (idx, sub);
                    }
                }

                reach[r + 1] = next;
            }

            long best = Inf;
            int bestIdx = -1, bestRoll = -1;
            foreach (int idx in reach[k].Keys.Order())
            {
                int rest = full - idx;
                if (rest == 0)
                {
                    if (0 < best)
                    {
                        best = 0;
                        bestIdx = idx;
                        bestRoll = -1;
                    }

                    continue;
                }

                foreach (int ri in _p.RollSources)
                {
                    var spec = _p.Sources[ri];
                    long len = MinLen(G(spec.Axis), spec.W, rest);
                    if (len >= Inf || NormalizedProblem.OrderedLength(spec, len) is not long ordered) continue;
                    long area = spec.W * ordered;
                    if (area < best)
                    {
                        best = area;
                        bestIdx = idx;
                        bestRoll = ri;
                    }
                }
            }

            outcome.Completed = true;
            if (bestIdx < 0)
            {
                outcome.ProvenInfeasible = true;
                outcome.Reason = "Exhaustive guillotine search proved that no plan satisfies the sources and supplier limits.";
                return outcome;
            }

            outcome.MinPurchasedAreaUnits = best;
            outcome.Reason = "Completed: minimum purchased area proven over all guillotine plans and source assignments.";
            outcome.Plan = BuildPlan(reach, rem, bestIdx, bestRoll, full);
        }
        catch (AbortException ex)
        {
            outcome.Completed = false;
            outcome.Reason = ex.Message;
        }
        finally
        {
            outcome.States = _states;
            outcome.ElapsedMs = sw.ElapsedMilliseconds;
        }

        return outcome;
    }

    private PackingState BuildPlan(Dictionary<int, (int Prev, int Sub)>[] reach, int[] rem, int bestIdx, int bestRoll, int full)
    {
        var used = new List<(int Source, int Sub)>();
        int cur = bestIdx;
        for (int r = rem.Length - 1; r >= 0; r--)
        {
            var (prev, sub) = reach[r + 1][cur];
            if (sub != 0) used.Add((rem[r], sub));
            cur = prev;
        }

        used.Reverse();
        int rest = full - bestIdx;
        if (bestRoll >= 0) used.Add((bestRoll, rest));
        var st = PackingState.Create(_p, used.Select(u => u.Source).ToList(), "exact");
        st.Origin = "exact";
        var queues = new Queue<int>[_t];
        for (int t = 0; t < _t; t++) queues[t] = new Queue<int>();
        foreach (var inst in _p.Instances) queues[inst.Type].Enqueue(inst.Index);

        for (int li = 0; li < used.Count; li++)
        {
            var layout = st.Layouts[li];
            int g = G(layout.Spec.Axis);
            var root = layout.Root;
            if (layout.IsRoll)
            {
                long len = MinLen(g, layout.Spec.W, used[li].Sub);
                if (len < layout.Bound)
                {
                    root.Split(CutAxis.Horizontal, len);
                    root = root.A!;
                }
            }

            Build(st, li, root, g, used[li].Sub, queues);
            layout.RebuildFree();
        }

        return st;
    }

    private void Build(PackingState st, int li, Node node, int g, int s, Queue<int>[] queues)
    {
        long wn = Floor(Normal(g, s), node.R.W);
        if (!_memo.TryGetValue((g, s, wn), out var e)) MinLen(g, node.R.W, s);
        e = _memo[(g, s, wn)];
        if (e.Len > node.R.H) throw new InvalidOperationException("Exact reconstruction does not fit its region.");
        switch (e.Kind)
        {
            case KBase:
            {
                int t = _single[s];
                var o = _ori[g][t][e.Ori];
                var pn = Node.CarvePiece(node, o.W, o.H, horizontalFirst: true, newFree: null);
                st.Attach(li, pn, queues[t].Dequeue(), o.Rotated);
                break;
            }
            case KHorizontal:
            {
                long a = MinLen(g, wn, e.Sub);
                node.Split(CutAxis.Horizontal, node.R.Y + a);
                Build(st, li, node.A!, g, e.Sub, queues);
                Build(st, li, node.B!, g, s - e.Sub, queues);
                break;
            }
            case KVertical:
                node.Split(CutAxis.Vertical, node.R.X + e.P);
                Build(st, li, node.A!, g, e.Sub, queues);
                Build(st, li, node.B!, g, s - e.Sub, queues);
                break;
            default:
                throw new InvalidOperationException("Exact reconstruction reached an infeasible state.");
        }
    }
}

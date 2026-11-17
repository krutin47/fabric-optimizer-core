using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

internal enum OrderKind
{
    AreaDesc,
    MaxSideDesc,
    PerimeterDesc,
    WidthDesc,
    LengthDesc,
    ConstrainedFirst,
    Random,
}

/// <summary>Multi-start piece orderings (spec §6 "candidate diversity").</summary>
internal static class PieceOrdering
{
    public static readonly OrderKind[] Deterministic =
    [
        OrderKind.AreaDesc, OrderKind.MaxSideDesc, OrderKind.ConstrainedFirst,
        OrderKind.LengthDesc, OrderKind.WidthDesc, OrderKind.PerimeterDesc,
    ];

    /// <summary>
    /// Orders <paramref name="instances"/>. Ties are broken by type index then instance index (deterministic);
    /// <paramref name="noise"/> &gt; 0 applies seeded adjacent swaps to perturb the order.
    /// </summary>
    public static int[] Build(NormalizedProblem p, IReadOnlyList<int> instances, OrderKind kind, IReadOnlyList<SourceSpec> sources, Rng? rng, double noise)
    {
        var arr = instances.ToArray();
        if (kind == OrderKind.Random)
        {
            if (rng is not null) rng.Shuffle(arr);
            return arr;
        }

        int[]? flexibility = kind == OrderKind.ConstrainedFirst ? Flexibility(p, sources) : null;
        long Key(int inst)
        {
            var t = p.TypeOf(inst);
            return kind switch
            {
                OrderKind.AreaDesc => t.FootArea,
                OrderKind.MaxSideDesc => t.MaxSide,
                OrderKind.PerimeterDesc => t.FootW + t.FootL,
                OrderKind.WidthDesc => t.FootW,
                OrderKind.LengthDesc => t.FootL,
                _ => 0,
            };
        }

        Array.Sort(arr, (a, b) =>
        {
            int c;
            if (flexibility is not null)
            {
                c = flexibility[p.Instances[a].Type].CompareTo(flexibility[p.Instances[b].Type]);
                if (c != 0) return c;
                c = p.TypeOf(b).FootArea.CompareTo(p.TypeOf(a).FootArea);
                if (c != 0) return c;
            }
            else
            {
                c = Key(b).CompareTo(Key(a));
                if (c != 0) return c;
                c = p.TypeOf(b).FootArea.CompareTo(p.TypeOf(a).FootArea);
                if (c != 0) return c;
            }

            c = p.Instances[a].Type.CompareTo(p.Instances[b].Type);
            return c != 0 ? c : a.CompareTo(b);
        });

        if (rng is not null && noise > 0)
        {
            for (int i = 0; i + 1 < arr.Length; i++)
                if (rng.NextDouble() < noise)
                    (arr[i], arr[i + 1]) = (arr[i + 1], arr[i]);
        }

        return arr;
    }

    /// <summary>Number of (source, legal orientation) pairs that physically fit — fewer = more constrained.</summary>
    private static int[] Flexibility(NormalizedProblem p, IReadOnlyList<SourceSpec> sources)
    {
        var f = new int[p.Types.Length];
        foreach (var t in p.Types)
        {
            int count = 0;
            foreach (var s in sources)
                foreach (var o in t.For(s.Axis))
                    if (o.W <= s.W && (s.IsRoll || o.H <= s.L))
                        count++;
            f[t.Index] = count;
        }

        return f;
    }
}

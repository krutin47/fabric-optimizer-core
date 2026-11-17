using DreamGlass.Optimizer.Exact;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

/// <summary>Valid lower bounds on newly purchased area (objective tier 1). Bounds, never feasibility proofs.</summary>
internal static class Bounds
{
    public static (long? AreaUnits, string Method) PurchasedArea(NormalizedProblem p, ExactOutcome? exact)
    {
        if (exact is { Completed: true, ProvenInfeasible: false, MinPurchasedAreaUnits: long proven })
            return (proven, "ExactGuillotineDP");

        // Pieces that fit no remnant in any legal orientation must be cut from the roll.
        long forcedArea = 0;
        var forced = new List<PieceType>();
        foreach (var inst in p.Instances)
        {
            var t = p.Types[inst.Type];
            bool fitsRemnant = p.RemnantSources.Any(ri =>
            {
                var s = p.Sources[ri];
                return t.For(s.Axis).Any(o => o.W <= s.W && o.H <= s.L);
            });
            if (fitsRemnant) continue;
            forcedArea += t.FootArea;
            forced.Add(t);
        }

        long need = Math.Max(forcedArea, p.RequiredArea - p.TotalRemnantArea);
        if (need <= 0) return (0, "AreaRelaxation");

        long? best = null;
        foreach (int ri in p.RollSources)
        {
            var roll = p.Sources[ri];
            long len = (need + roll.W - 1) / roll.W;
            bool ok = true;
            foreach (var t in forced)
            {
                long minH = long.MaxValue;
                foreach (var o in t.For(roll.Axis))
                    if (o.W <= roll.W) minH = Math.Min(minH, o.H);
                if (minH == long.MaxValue)
                {
                    ok = false;
                    break;
                }

                len = Math.Max(len, minH);
            }

            if (!ok || NormalizedProblem.OrderedLength(roll, len) is not long ordered) continue;
            long area = roll.W * ordered;
            best = best is long b ? Math.Min(b, area) : area;
        }

        return (best, "AreaRelaxation");
    }
}

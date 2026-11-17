using DreamGlass.Optimizer.Exact;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

/// <summary>Lower bounds on what any plan must buy. <see cref="Proven"/> is true only for a completed exact search.</summary>
internal readonly record struct PurchaseBounds(long? AreaUnits, decimal? Cost, string Method, bool Proven);

/// <summary>
/// Valid lower bounds on newly purchased area and on purchase cost. Bounds, never feasibility proofs.
/// <para>
/// Without a completed exact search the bound is a relaxation, computed per roll offer and minimised over the offers
/// (a plan buys from exactly one). The roll length must cover:
/// </para>
/// <list type="bullet">
/// <item><b>Area:</b> the area that cannot come from the warehouse, divided by the roll width.</item>
/// <item><b>Longest piece:</b> every piece that fits no remnant lies on the roll, so the roll is at least as long as
///       the shortest legal way to lay each of them.</item>
/// <item><b>Wide pieces:</b> two pieces that are each wider than half the roll in every legal orientation can never lie
///       side by side, so the roll is at least as long as all of them stacked.</item>
/// </list>
/// </summary>
internal static class Bounds
{
    public const string ExactMethod = "ExactGuillotineDP";
    public const string RelaxationMethod = "AreaAndWidePieceRelaxation";

    public static PurchaseBounds Compute(NormalizedProblem p, ExactOutcome? exact)
    {
        if (exact is { Completed: true, ProvenInfeasible: false, MinPurchasedAreaUnits: long provenArea })
            return new PurchaseBounds(provenArea, exact.MinPurchaseCost, ExactMethod, true);

        var (forced, need) = ForcedOntoRoll(p);
        if (need <= 0) return new PurchaseBounds(0, 0m, RelaxationMethod, false);

        long? bestArea = null;
        decimal? bestCost = null;
        foreach (int ri in p.RollSources)
        {
            if (ForRoll(p, p.Sources[ri], need, forced) is not var (area, cost)) continue;
            if (bestArea is not long ba || area < ba) bestArea = area;
            if (bestCost is not decimal bc || cost < bc) bestCost = cost;
        }

        return new PurchaseBounds(bestArea, bestCost, RelaxationMethod, false);
    }

    /// <summary>
    /// The pieces that fit no remnant in any legal orientation (they must be cut from the roll), and the area any
    /// plan has to buy: the larger of those pieces' area and the job's area beyond the whole warehouse offer.
    /// </summary>
    public static (List<PieceType> Forced, long NeedArea) ForcedOntoRoll(NormalizedProblem p)
    {
        long forcedArea = 0;
        var forced = new List<PieceType>();
        foreach (var inst in p.Instances)
        {
            var t = p.Types[inst.Type];
            bool fitsRemnant = false;
            foreach (int ri in p.RemnantSources)
            {
                var s = p.Sources[ri];
                foreach (var o in t.For(s.Axis))
                    if (o.W <= s.W && o.H <= s.L) fitsRemnant = true;
                if (fitsRemnant) break;
            }

            if (fitsRemnant) continue;
            forcedArea += t.FootArea;
            forced.Add(t);
        }

        return (forced, Math.Max(forcedArea, p.RequiredArea - p.TotalRemnantArea));
    }

    /// <summary>
    /// Least area and cost a plan must buy from <paramref name="roll"/> when at least <paramref name="needArea"/> and
    /// the <paramref name="forced"/> pieces go on it; null when the roll cannot hold them within the supplier's limits.
    /// </summary>
    public static (long Area, decimal Cost)? ForRoll(NormalizedProblem p, SourceSpec roll, long needArea, IReadOnlyList<PieceType> forced)
    {
        if (needArea <= 0 && forced.Count == 0) return (0, 0m);
        if (MinRollLength(roll, Math.Max(0, needArea), forced) is not long len) return null;
        if (NormalizedProblem.OrderedLength(roll, len) is not long ordered) return null;
        long area = roll.W * ordered;
        return (area, Units.AreaToM2(area, p.Scale) * roll.Offer!.PricePerSquareMetre);
    }

    /// <summary>Shortest length of <paramref name="roll"/> any plan needs, or null when a forced piece cannot lie on it at all.</summary>
    internal static long? MinRollLength(SourceSpec roll, long needArea, IReadOnlyList<PieceType> forced)
    {
        long len = (needArea + roll.W - 1) / roll.W;
        long wideStack = 0;
        foreach (var t in forced)
        {
            long minH = long.MaxValue;
            bool alwaysWide = true;
            foreach (var o in t.For(roll.Axis))
            {
                if (o.W > roll.W) continue;
                minH = Math.Min(minH, o.H);
                if (2 * o.W <= roll.W) alwaysWide = false;
            }

            if (minH == long.MaxValue) return null;
            len = Math.Max(len, minH);
            if (alwaysWide) wideStack += minH;
        }

        return Math.Max(len, wideStack);
    }
}

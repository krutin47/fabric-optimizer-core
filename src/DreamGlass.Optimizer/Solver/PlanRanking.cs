using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Solver;

/// <summary>
/// Objective comparator (spec §5) and Pareto helpers. Validity is not a tier: only validated plans are ever
/// compared. The tier order comes from <see cref="ObjectivePolicy"/> so the pending business decision stays in
/// configuration rather than code.
/// </summary>
public static class PlanRanking
{
    /// <summary>Negative when <paramref name="a"/> is preferred.</summary>
    public static int Compare(PlanMetrics a, PlanMetrics b, ObjectivePolicy policy)
    {
        foreach (var tier in policy.Tiers)
        {
            int r = tier switch
            {
                ObjectiveCriterion.PurchasedArea => Bucket(a.PurchasedAreaM2, policy.PurchasedAreaTieToleranceM2)
                    .CompareTo(Bucket(b.PurchasedAreaM2, policy.PurchasedAreaTieToleranceM2)),
                ObjectiveCriterion.UnusableScrapArea => Bucket(a.UnusableScrapAreaM2, policy.ScrapTieToleranceM2)
                    .CompareTo(Bucket(b.UnusableScrapAreaM2, policy.ScrapTieToleranceM2)),
                ObjectiveCriterion.LargestReusableRemnantArea => b.LargestReusableAreaM2.CompareTo(a.LargestReusableAreaM2),
                ObjectiveCriterion.ThinFragmentCount => a.ThinFragmentCount.CompareTo(b.ThinFragmentCount),
                ObjectiveCriterion.PurchaseCost => a.PurchaseCost.CompareTo(b.PurchaseCost),
                ObjectiveCriterion.TotalCuts => a.TotalCuts.CompareTo(b.TotalCuts),
                ObjectiveCriterion.TotalResidualArea => a.TotalResidualAreaM2.CompareTo(b.TotalResidualAreaM2),
                ObjectiveCriterion.ExistingStockAreaConsumed => a.ExistingStockAreaConsumedM2.CompareTo(b.ExistingStockAreaConsumedM2),
                ObjectiveCriterion.ReusableResidualArea => b.ReusableResidualAreaM2.CompareTo(a.ReusableResidualAreaM2),
                ObjectiveCriterion.SourceCount => a.SourcesUsed.CompareTo(b.SourcesUsed),
                ObjectiveCriterion.RollLengthUsed => (a.ConsumedLengthMm ?? 0).CompareTo(b.ConsumedLengthMm ?? 0),
                _ => 0,
            };
            if (r != 0) return r;
        }

        return 0;
    }

    /// <summary>Buckets make "near-equivalent" purchases tie while keeping the comparator transitive.</summary>
    private static decimal Bucket(decimal value, decimal tolerance) => tolerance > 0 ? Math.Floor(value / tolerance) : value;

    /// <summary>
    /// Pareto dominance over the displayed trade-off vector: purchased area, unusable scrap, largest reusable
    /// remnant (maximised), purchase cost, cut count and existing warehouse stock committed.
    /// </summary>
    public static bool Dominates(PlanMetrics a, PlanMetrics b)
    {
        bool noWorse = a.PurchasedAreaM2 <= b.PurchasedAreaM2
                       && a.UnusableScrapAreaM2 <= b.UnusableScrapAreaM2
                       && a.LargestReusableAreaM2 >= b.LargestReusableAreaM2
                       && a.PurchaseCost <= b.PurchaseCost
                       && a.TotalCuts <= b.TotalCuts
                       && a.ExistingStockAreaConsumedM2 <= b.ExistingStockAreaConsumedM2;
        bool better = a.PurchasedAreaM2 < b.PurchasedAreaM2
                      || a.UnusableScrapAreaM2 < b.UnusableScrapAreaM2
                      || a.LargestReusableAreaM2 > b.LargestReusableAreaM2
                      || a.PurchaseCost < b.PurchaseCost
                      || a.TotalCuts < b.TotalCuts
                      || a.ExistingStockAreaConsumedM2 < b.ExistingStockAreaConsumedM2;
        return noWorse && better;
    }

    internal sealed class PlanComparer(ObjectivePolicy policy) : IComparer<CandidateCutPlan>
    {
        public int Compare(CandidateCutPlan? x, CandidateCutPlan? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            int r = PlanRanking.Compare(x.Metrics, y.Metrics, policy);
            return r != 0 ? r : string.CompareOrdinal(x.CandidateId, y.CandidateId);
        }
    }

    internal static string MetricKey(PlanMetrics m) =>
        $"{m.PurchasedAreaM2}|{m.UnusableScrapAreaM2}|{m.LargestReusableAreaM2}|{m.PurchaseCost}|{m.TotalCuts}|{m.ExistingStockAreaConsumedM2}";
}

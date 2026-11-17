using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Solver;

namespace DreamGlass.Optimizer.Tests;

/// <summary>
/// Example-based objective tests (spec §5, §12.1). The first part documents the spec's <b>proposed</b> comparator
/// (kept for comparison); the last part covers <see cref="ObjectivePolicy.CheapestFirst"/>, the order the product owner
/// approved on 12 Nov 2026 and the default.
/// </summary>
[Trait("Objective", "proposed-v1-pending-approval")]
public class ObjectiveTests
{
    private static PlanMetrics M(decimal purchased = 0, decimal scrap = 0, decimal largest = 0, int thin = 0, decimal cost = 0, int cuts = 0, decimal residual = 0, decimal stock = 0, decimal? used = null) =>
        new()
        {
            ConsumedLengthMm = used,
            PurchasedAreaM2 = purchased,
            UnusableScrapAreaM2 = scrap,
            LargestReusableAreaM2 = largest,
            ThinFragmentCount = thin,
            PurchaseCost = cost,
            TotalCuts = cuts,
            TotalResidualAreaM2 = residual,
            ExistingStockAreaConsumedM2 = stock,
        };

    private static int Proposed(PlanMetrics a, PlanMetrics b) => PlanRanking.Compare(a, b, ObjectivePolicy.ProposedV1);

    [Fact]
    public void Less_newly_purchased_area_outranks_less_scrap()
    {
        Assert.True(Proposed(M(purchased: 2.8m, scrap: 0.5m), M(purchased: 5.6m, scrap: 0)) < 0);
    }

    [Fact]
    public void At_equal_purchase_less_scrap_wins_then_larger_remnant_then_fewer_thin_strips()
    {
        Assert.True(Proposed(M(purchased: 2.8m, scrap: 0.1m), M(purchased: 2.8m, scrap: 0.2m)) < 0);
        Assert.True(Proposed(M(purchased: 2.8m, scrap: 0.1m, largest: 1.0m), M(purchased: 2.8m, scrap: 0.1m, largest: 0.3m)) < 0);
        Assert.True(Proposed(M(largest: 1.0m, thin: 0), M(largest: 1.0m, thin: 3)) < 0);
    }

    [Fact]
    public void Fragmentation_tie_break_one_large_offcut_beats_skinny_strips_at_equal_area_and_cost()
    {
        var oneLarge = M(purchased: 2.8m, scrap: 0, largest: 1.2m, thin: 0, cost: 35m, cuts: 6);
        var skinny = M(purchased: 2.8m, scrap: 0, largest: 0.4m, thin: 2, cost: 35m, cuts: 5);
        Assert.True(Proposed(oneLarge, skinny) < 0);
    }

    [Fact]
    public void Cost_then_cuts_break_remaining_ties()
    {
        Assert.True(Proposed(M(purchased: 2.8m, cost: 25.2m, cuts: 9), M(purchased: 2.8m, cost: 35m, cuts: 1)) < 0);
        Assert.True(Proposed(M(cuts: 3), M(cuts: 4)) < 0);
    }

    [Fact]
    public void Purchase_tolerance_makes_near_equal_purchases_fall_through_to_scrap()
    {
        var tolerant = ObjectivePolicy.ProposedV1 with { PurchasedAreaTieToleranceM2 = 1m };
        var a = M(purchased: 2.8m, scrap: 0.4m);
        var b = M(purchased: 2.9m, scrap: 0.1m);
        Assert.True(Proposed(a, b) < 0);
        Assert.True(PlanRanking.Compare(b, a, tolerant) < 0);
    }

    [Fact]
    public void Documented_conflict_historical_waste_first_versus_proposed()
    {
        // A: buys 1 m of roll, leaves 1.4 m² residual. B: warehouse-only, but leaves 2.0 m² residual in the remnant.
        var a = M(purchased: 2.8m, residual: 1.4m, cost: 35m);
        var b = M(purchased: 0m, residual: 2.0m, stock: 3.0m);
        Assert.True(Proposed(b, a) < 0); // proposed-v1: no purchase wins
        Assert.True(PlanRanking.Compare(a, b, ObjectivePolicy.HistoricalWasteFirst) < 0); // historical: less leftover wins
    }

    [Fact]
    public void Pareto_dominance_requires_no_worse_on_every_displayed_tradeoff()
    {
        Assert.True(PlanRanking.Dominates(M(purchased: 2.8m, cuts: 3), M(purchased: 2.8m, cuts: 4)));
        Assert.False(PlanRanking.Dominates(M(purchased: 2.8m, cuts: 5), M(purchased: 5.6m, cuts: 4)));
        Assert.False(PlanRanking.Dominates(M(stock: 1m), M(stock: 0m)));
    }

    [Fact]
    public void Proposed_policy_is_flagged_as_unapproved_in_results()
    {
        var r = T.RunOk(T.Job([T.Piece("P", 500, 500)], [T.Roll("r", 2800)]), new OptimizationOptions { Objective = ObjectivePolicy.ProposedV1 });
        Assert.False(r.ObjectiveApproved);
        Assert.Equal("proposed-v1", r.ObjectiveVersion);
        Assert.Contains(r.Diagnostics.Warnings, w => w.Contains("pending product-owner approval"));
    }

    // ---- cheapest-first: the order the product owner approved on 12 Nov 2026 ------------------------------------------

    private static int Cheapest(PlanMetrics a, PlanMetrics b) => PlanRanking.Compare(a, b, ObjectivePolicy.CheapestFirst);

    [Fact]
    public void Cheapest_first_is_the_approved_default()
    {
        var r = T.RunOk(T.Job([T.Piece("P", 500, 500)], [T.Roll("r", 2800)]));
        Assert.True(r.ObjectiveApproved);
        Assert.Equal("cheapest-first", r.ObjectiveVersion);
        Assert.DoesNotContain(r.Diagnostics.Warnings, w => w.Contains("pending product-owner approval"));
    }

    [Fact]
    public void Cheapest_first_puts_price_above_fabric_area_and_scrap()
    {
        // The sketch job: 4 m of the 1,400 mm roll (€50.40) beats 2 m of the 2,800 mm roll (€70) with the same 5.6 m²
        // and less scrap.
        var narrow = M(purchased: 5.6m, cost: 50.40m, scrap: 0.1251m, largest: 0.658m);
        var wide = M(purchased: 5.6m, cost: 70m, scrap: 0.072m, largest: 0.3807m);
        Assert.True(Cheapest(narrow, wide) < 0);
    }

    [Fact]
    public void Cheapest_first_ignores_a_tiny_scrap_difference_in_favour_of_a_bigger_keepable_leftover()
    {
        // Same price; 0.0041 m² more scrap (within the 0.01 m² tolerance) but one 470 × 1,400 leftover instead of
        // 510 × 590 + 420 × 860.
        var sketch = M(cost: 50.40m, scrap: 0.1251m, largest: 0.658m, used: 3530m, cuts: 8);
        var before = M(cost: 50.40m, scrap: 0.1210m, largest: 0.3612m, used: 3950m, cuts: 10);
        Assert.True(Cheapest(sketch, before) < 0);
        // With no tolerance the exact scrap decides, as the spec's proposal did.
        Assert.True(PlanRanking.Compare(sketch, before, ObjectivePolicy.CheapestFirst with { ScrapTieToleranceM2 = 0 }) > 0);
    }

    [Fact]
    public void Cheapest_first_still_prefers_clearly_less_scrap_over_a_bigger_leftover()
    {
        var lessScrap = M(cost: 50m, scrap: 0.05m, largest: 0.1m);
        var biggerLeftover = M(cost: 50m, scrap: 0.12m, largest: 0.9m);
        Assert.True(Cheapest(lessScrap, biggerLeftover) < 0);
    }

    [Fact]
    public void Cheapest_first_then_prefers_less_roll_length_then_fewer_cuts()
    {
        Assert.True(Cheapest(M(cost: 50m, scrap: 0.1m, largest: 0.5m, used: 3530m, cuts: 12), M(cost: 50m, scrap: 0.1m, largest: 0.5m, used: 3950m, cuts: 8)) < 0);
        Assert.True(Cheapest(M(cost: 50m, scrap: 0.1m, largest: 0.5m, used: 3530m, cuts: 8), M(cost: 50m, scrap: 0.1m, largest: 0.5m, used: 3530m, cuts: 9)) < 0);
    }
}

using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Solver;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Spec §10: property-based randomized tests — every generated candidate passes independent validation.</summary>
public class PropertyTests
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 30).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Every_returned_candidate_is_valid_ranked_and_honest(int seed)
    {
        var rng = new Random(seed);
        var job = Fixtures.Random(rng, instances: 1 + rng.Next(45), withRolls: rng.Next(5) != 0);
        var r = T.Run(job, T.Det(work: 150, stagnation: 80));
        Assert.NotEqual(RunStatus.Failed, r.Status);
        if (r.Status == RunStatus.InvalidInput) return; // e.g. warehouse-only job with a piece larger than every remnant
        Assert.Equal(0, r.Diagnostics.PlansRejectedByValidator);
        if (r.Status != RunStatus.Succeeded) return;

        T.AllValid(r, job);
        Assert.InRange(r.Candidates.Count, 1, job.Search.MaxCandidates);
        Assert.Equal(r.Candidates.Count, r.Candidates.Select(c => c.CandidateId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, r.Candidates.Count), r.Candidates.Select(c => c.Rank));
        for (int i = 1; i < r.Candidates.Count; i++)
            Assert.True(PlanRanking.Compare(r.Candidates[i - 1].Metrics, r.Candidates[i].Metrics, ObjectivePolicy.CheapestFirst) <= 0);
        Assert.All(r.Candidates, c => Assert.NotEmpty(c.Tags));
        Assert.All(r.Candidates, c => Assert.True(c.Bound is null || c.Bound.GapM2 >= 0));
        Assert.Contains("Best by objective", r.Candidates[0].Tags);
    }

    [Fact]
    public void Property_generator_mostly_produces_solvable_jobs()
    {
        int solved = 0;
        foreach (int seed in Enumerable.Range(1, 30))
        {
            var rng = new Random(seed);
            var job = Fixtures.Random(rng, instances: 1 + rng.Next(45), withRolls: rng.Next(5) != 0);
            if (T.Run(job, T.Det(work: 60, stagnation: 30)).Status == RunStatus.Succeeded) solved++;
        }

        Assert.True(solved >= 20, $"only {solved}/30 random jobs were solved; property tests would be vacuous");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_corruptions_are_always_rejected(int seed)
    {
        var rng = new Random(1000 + seed);
        var job = Fixtures.Random(rng, instances: 2 + rng.Next(20));
        var r = T.Run(job, T.Det(work: 80, stagnation: 40));
        if (r.Status != RunStatus.Succeeded) return;
        var plan = T.Best(r);
        var validator = new CutPlanValidator();
        for (int k = 0; k < 8; k++)
        {
            var bad = Corrupt(plan, rng);
            if (bad is null) continue;
            Assert.False(validator.Validate(job, bad).Passed, $"corruption {k} accepted");
        }
    }

    private static CandidateCutPlan? Corrupt(CandidateCutPlan plan, Random rng)
    {
        var p = plan.Placements[rng.Next(plan.Placements.Count)];
        switch (rng.Next(9))
        {
            case 0:
                return plan with { Placements = plan.Placements.Select(x => x == p ? x with { Footprint = x.Footprint with { X = x.Footprint.X + 1 } } : x).ToList() };
            case 1:
                return plan with { Placements = plan.Placements.Where(x => x != p).ToList() };
            case 2:
                return plan with { Metrics = plan.Metrics with { PurchasedAreaM2 = plan.Metrics.PurchasedAreaM2 + 0.01m } };
            case 3:
                return plan.PredictedResiduals.Count == 0 ? null : plan with { PredictedResiduals = plan.PredictedResiduals.Select((x, i) => i == 0 ? x with { Eligible = !x.Eligible } : x).ToList() };
            case 4:
                return plan.CutSteps.Count == 0 ? null : plan with { CutSteps = plan.CutSteps.Skip(1).ToList() };
            case 5:
                return plan with { Sources = plan.Sources.Select(s => s with { LengthMm = s.LengthMm + 1 }).ToList() };
            case 6:
                return plan.Purchase is null ? null : plan with { Purchase = plan.Purchase with { OrderedLengthMetres = plan.Purchase.OrderedLengthMetres + 1 } };
            case 7:
            {
                var tree = plan.CutTrees[rng.Next(plan.CutTrees.Count)];
                var cut = tree.Nodes.FirstOrDefault(n => n.Kind == CutNodeKind.Cut);
                if (cut is null) return null;
                var swapped = cut with { Axis = cut.Axis == CutAxis.Vertical ? CutAxis.Horizontal : CutAxis.Vertical };
                return plan with { CutTrees = plan.CutTrees.Select(t => t == tree ? t with { Nodes = t.Nodes.Select(n => n == cut ? swapped : n).ToList() } : t).ToList() };
            }
            default:
                return plan with { Placements = plan.Placements.Select(x => x == p ? x with { PieceInstanceId = x.PieceId + "#0" } : x).ToList() };
        }
    }
}

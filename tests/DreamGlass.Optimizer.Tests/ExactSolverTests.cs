using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Exact;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Solver;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Spec §10 "Small known-optimum": the exact DP against hand-computed and brute-force optima.</summary>
public class ExactSolverTests
{
    private static ExactOutcome Solve(OptimizationSnapshot job, ExactSolverOptions? o = null)
    {
        var p = Normalizer.Normalize(job);
        var budget = new SearchBudget(60_000, long.MaxValue, ExecutionMode.Deterministic, CancellationToken.None);
        return ExactGuillotineSolver.TrySolve(p, o ?? new ExactSolverOptions(), budget, 60_000);
    }

    [Fact]
    public void Hand_computed_strip_optimum()
    {
        // Two 1400×1000 side by side, then a full-width 2800×500 → 1500 mm → 2 m.
        var job = T.Job([T.Piece("A", 1400, 1000, q: 2, o: PatternOrientation.Vertical), T.Piece("B", 2800, 500, o: PatternOrientation.Vertical)], [T.Roll("r", 2800)]);
        var outcome = Solve(job);
        Assert.True(outcome.Completed);
        var roll = outcome.Plan!.Layouts.Single(l => l.IsRoll);
        Assert.Equal(15_000, roll.Consumed); // units of 0.1 mm
        Assert.Equal(2800L * 10 * 20_000, outcome.MinPurchasedAreaUnits);
    }

    [Fact]
    public void Exact_minimum_length_matches_an_independent_grid_brute_force()
    {
        var rng = new Random(2024);
        for (int round = 0; round < 40; round++)
        {
            int n = 1 + rng.Next(5);
            bool directional = rng.Next(2) == 0;
            var pieces = Enumerable.Range(0, n).Select(i => T.Piece($"P{i}", 100 * (1 + rng.Next(4)), 100 * (1 + rng.Next(4)), o: (PatternOrientation)rng.Next(3))).ToList();
            var job = T.Job(pieces, [T.Roll("r", 600)], directional: directional);
            var outcome = Solve(job);
            Assert.True(outcome.Completed);
            long exactMm = outcome.Plan!.Layouts.Single(l => l.IsRoll).Consumed / 10;
            Assert.Equal(BruteForce(pieces, directional, 600), exactMm);
        }
    }

    /// <summary>Independent formulation: every cut position on a 100 mm grid, no normal-pattern or width flooring.</summary>
    private static long BruteForce(List<PieceRequest> pieces, bool directional, long width)
    {
        var memo = new Dictionary<(long, int), long>();
        int n = pieces.Count;
        long Solve(long w, int mask)
        {
            if (memo.TryGetValue((w, mask), out long v)) return v;
            long best = long.MaxValue / 4;
            if ((mask & (mask - 1)) == 0)
            {
                var p = pieces[System.Numerics.BitOperations.TrailingZeroCount(mask)];
                bool canUnrotated = !directional || p.Orientation != PatternOrientation.Horizontal; // roll pattern along length
                bool canRotated = !directional || p.Orientation != PatternOrientation.Vertical;
                if (canUnrotated && p.WidthMm <= w) best = Math.Min(best, (long)p.LengthMm);
                if (canRotated && p.LengthMm <= w) best = Math.Min(best, (long)p.WidthMm);
            }
            else
            {
                for (int a = (mask - 1) & mask; a > 0; a = (a - 1) & mask)
                {
                    int b = mask & ~a;
                    best = Math.Min(best, Solve(w, a) + Solve(w, b));
                    for (long x = 100; x < w; x += 100) best = Math.Min(best, Math.Max(Solve(x, a), Solve(w - x, b)));
                }
            }

            return memo[(w, mask)] = best;
        }

        return Solve(width, (1 << n) - 1);
    }

    [Fact]
    public void Exact_bound_never_exceeds_any_verified_candidate_and_the_best_attains_it()
    {
        var rng = new Random(77);
        for (int round = 0; round < 12; round++)
        {
            var job = Fixtures.Random(rng, instances: 2 + rng.Next(7), maxStock: 3);
            var r = T.Run(job, T.Det(work: 200, stagnation: 100));
            if (r.Status == RunStatus.InvalidInput) continue;
            Assert.Equal(RunStatus.Succeeded, r.Status);
            T.AllValid(r, job);
            var exact = r.Diagnostics.Exact!;
            if (!exact.Completed) continue;
            foreach (var c in r.Candidates) Assert.True(c.Metrics.PurchasedAreaM2 >= exact.ProvenMinPurchasedAreaM2);
            Assert.Equal(exact.ProvenMinPurchasedAreaM2, T.Best(r).Metrics.PurchasedAreaM2);
            Assert.Equal(QualityStatus.BoundedGap, T.Best(r).QualityStatus);
        }
    }

    [Fact]
    public void Aborted_exact_search_makes_no_optimality_claim()
    {
        var job = T.Job([T.Piece("A", 430, 610, q: 3), T.Piece("B", 900, 350, q: 2)], [T.Roll("r", 2800)]);
        var outcome = Solve(job, new ExactSolverOptions { MaxStates = 5 });
        Assert.True(outcome.Attempted);
        Assert.False(outcome.Completed);
        Assert.Null(outcome.MinPurchasedAreaUnits);
        Assert.Null(outcome.Plan);

        var r = T.RunOk(job, T.Det() with { Exact = new ExactSolverOptions { MaxStates = 5 } });
        Assert.NotEqual("ExactGuillotineDP", r.Diagnostics.PurchasedAreaLowerBoundMethod);
    }

    [Fact]
    public void Exact_plan_assigns_pieces_across_remnants_and_roll()
    {
        var job = T.Job(
            [T.Piece("S", 500, 500, o: PatternOrientation.Vertical), T.Piece("L", 1000, 1000, o: PatternOrientation.Vertical), T.Piece("X", 2000, 800, o: PatternOrientation.Vertical)],
            [T.Roll("r", 2800)],
            [T.Stock("A", 1000, 1000), T.Stock("B", 600, 600)]);
        var outcome = Solve(job);
        Assert.True(outcome.Completed);
        Assert.Equal(2800L * 10 * 10_000, outcome.MinPurchasedAreaUnits); // only X needs the roll: 1 m
    }
}

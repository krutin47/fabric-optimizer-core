using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Validation;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Fixture builders shared by the test suite.</summary>
internal static class T
{
    public static PieceRequest Piece(string id, decimal w, decimal l, int q = 1, PatternOrientation o = PatternOrientation.None, decimal pad = 0, Padding? padding = null) =>
        new()
        {
            Id = id,
            WidthMm = w,
            LengthMm = l,
            Quantity = q,
            Orientation = o,
            PaddingMm = padding ?? new Padding { Top = pad, Right = pad, Bottom = pad, Left = pad },
        };

    public static RollOffer Roll(string id, decimal width, decimal price = 10m, int? maxMetres = null, decimal? fixedLength = null, int minMetres = 1, int increment = 1) =>
        new()
        {
            OfferId = id,
            WidthMm = width,
            PricePerSquareMetre = price,
            Currency = "EUR",
            MaxOrderMetres = maxMetres,
            FixedLengthMm = fixedLength,
            MinOrderMetres = minMetres,
            OrderIncrementMetres = increment,
        };

    public static StockItem Stock(string id, decimal w, decimal l, PatternAxis axis = PatternAxis.Length, long version = 1) =>
        new() { StockId = id, WidthMm = w, LengthMm = l, PatternAxis = axis, Version = version, MaterialId = "fabric" };

    public static readonly RemnantPolicy DefaultPolicy = new() { MinWidthMm = 200, MinLengthMm = 300 };

    public static OptimizationSnapshot Job(
        IEnumerable<PieceRequest> pieces,
        IEnumerable<RollOffer>? offers = null,
        IEnumerable<StockItem>? stock = null,
        bool directional = true,
        RemnantPolicy? policy = null,
        bool noPolicy = false,
        int maxCandidates = 3,
        long seed = 7,
        int timeMs = 30_000) =>
        new()
        {
            JobRevisionId = "test",
            MaterialId = "fabric",
            MaterialDirectional = directional,
            Pieces = pieces.ToList(),
            RollOffers = (offers ?? []).ToList(),
            Stock = (stock ?? []).ToList(),
            MinimumReusableRemnant = noPolicy ? null : policy ?? DefaultPolicy,
            Search = new SearchSettings { TimeBudgetMs = timeMs, Seed = seed, MaxCandidates = maxCandidates },
        };

    /// <summary>Deterministic, work-bounded options so tests are fast and reproducible.</summary>
    public static OptimizationOptions Det(int work = 300, int stagnation = 300) =>
        new() { Mode = ExecutionMode.Deterministic, WorkBudget = work, StagnationLimit = stagnation, ValidateEveryKeptPlan = true };

    public static OptimizationResult Run(OptimizationSnapshot job, OptimizationOptions? options = null, CancellationToken ct = default) =>
        new HybridCutOptimizer().Optimize(job, options ?? Det(), ct);

    public static OptimizationResult RunOk(OptimizationSnapshot job, OptimizationOptions? options = null)
    {
        var r = Run(job, options);
        Assert.True(r.Status == RunStatus.Succeeded,
            $"Expected success, got {r.Status}: {string.Join("; ", r.InputErrors.Select(e => e.Code + " " + e.Message))} {r.Diagnostics.FailureMessage} {string.Join("; ", r.Diagnostics.InfeasibilityReasons)}");
        Assert.Equal(0, r.Diagnostics.PlansRejectedByValidator);
        AllValid(r, job);
        return r;
    }

    public static void AllValid(OptimizationResult r, OptimizationSnapshot job)
    {
        var v = new CutPlanValidator();
        foreach (var c in r.Candidates)
        {
            var outcome = v.Validate(job, c);
            Assert.True(outcome.Passed, $"{c.CandidateId}: " + string.Join("; ", outcome.Failures.Select(f => $"{f.Code} {f.Message}")));
            Assert.NotEqual(QualityStatus.OptimalProven, c.QualityStatus);
            var m = c.Metrics;
            Assert.Equal(m.PurchasedAreaM2 + m.ExistingStockAreaConsumedM2, m.RequiredCutAreaM2 + m.TotalResidualAreaM2);
            Assert.Equal(m.TotalResidualAreaM2, m.ReusableResidualAreaM2 + m.UnusableScrapAreaM2);
        }
    }

    public static CandidateCutPlan Best(OptimizationResult r) => r.Candidates.Single(c => c.Rank == 1);

    public static IEnumerable<string> Codes(PlanValidationOutcome o) => o.Failures.Select(f => f.Code);
}

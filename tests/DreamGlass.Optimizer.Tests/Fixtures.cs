using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Tests;

/// <summary>Seeded random job generator for property tests. Pieces always fit the widest roll (when present).</summary>
internal static class Fixtures
{
    public static OptimizationSnapshot Random(Random rng, int instances, bool? directional = null, bool withRolls = true, int maxStock = 4)
    {
        bool dir = directional ?? rng.Next(2) == 0;
        var pieces = new List<PieceRequest>();
        int left = instances, id = 0;
        while (left > 0)
        {
            int q = Math.Min(left, 1 + rng.Next(3));
            left -= q;
            var orientation = (PatternOrientation)rng.Next(3);
            decimal pad = rng.Next(4) == 0 ? 5 : 0;
            pieces.Add(new PieceRequest
            {
                Id = $"P{++id}",
                WidthMm = 100 + 10 * rng.Next(120),
                LengthMm = 100 + 10 * rng.Next(150),
                Quantity = q,
                Orientation = orientation,
                PaddingMm = new Padding { Top = pad, Right = pad, Bottom = pad * 2, Left = 0 },
            });
        }

        var stock = Enumerable.Range(1, rng.Next(maxStock + 1)).Select(i => new StockItem
        {
            StockId = $"R{i}",
            WidthMm = 300 + 50 * rng.Next(40),
            LengthMm = 300 + 50 * rng.Next(50),
            PatternAxis = rng.Next(2) == 0 ? PatternAxis.Length : PatternAxis.Width,
            Version = 1 + rng.Next(9),
            MaterialId = "fabric",
        }).ToList();

        var rolls = withRolls
            ? new List<RollOffer>
            {
                new() { OfferId = "roll-2800", WidthMm = 2800, PricePerSquareMetre = 12.5m, Currency = "EUR" },
                new() { OfferId = "roll-1600", WidthMm = 1600, PricePerSquareMetre = 11.75m, Currency = "EUR", OrderIncrementMetres = 1 + rng.Next(2) },
            }
            : [];

        return new OptimizationSnapshot
        {
            JobRevisionId = "random",
            MaterialId = "fabric",
            MaterialDirectional = dir,
            Pieces = pieces,
            RollOffers = rolls,
            Stock = stock,
            MinimumReusableRemnant = rng.Next(4) == 0 ? null : new RemnantPolicy { MinWidthMm = 150 + 50 * rng.Next(3), MinLengthMm = 300, MaxAspectRatio = rng.Next(2) == 0 ? 8 : null },
            Search = new SearchSettings { TimeBudgetMs = 30_000, Seed = rng.Next(), MaxCandidates = 1 + rng.Next(4) },
        };
    }
}

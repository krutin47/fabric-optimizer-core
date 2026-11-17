using System.Globalization;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;

namespace DreamGlass.Optimizer.Normalization;

/// <summary>
/// Pre-enqueue input validation (spec §3). Returns structured errors with JSON-style field paths and
/// remediation text; never throws for bad data.
/// </summary>
public static class InputValidator
{
    public sealed record Outcome(IReadOnlyList<InputError> Errors, IReadOnlyList<string> Warnings)
    {
        public bool IsValid => Errors.Count == 0;
    }

    public static Outcome Validate(OptimizationSnapshot? snapshot, OptimizationOptions? options = null)
    {
        var v = new Run(options ?? OptimizationOptions.Default);
        if (snapshot is null)
        {
            v.Error("INPUT_MISSING", "$", "No snapshot was supplied.", "Send a complete optimisation snapshot.");
            return v.Result();
        }

        v.Check(snapshot);
        return v.Result();
    }

    private sealed class Run(OptimizationOptions options)
    {
        private readonly List<InputError> _errors = [];
        private readonly List<string> _warnings = [];
        private readonly OptimizerLimits _limits = options.Limits ?? new OptimizerLimits();
        private long _scale = 10;

        public Outcome Result() => new(_errors, _warnings);

        public void Error(string code, string path, string message, string remediation) =>
            _errors.Add(new InputError(code, path, message, remediation));

        private static string F(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        private string Mm(long units) => F(Units.ToMm(units, _scale));

        public void Check(OptimizationSnapshot s)
        {
            if (s.SchemaVersion != 1)
                Error("SCHEMA_UNSUPPORTED", "$.schemaVersion", $"Schema version {s.SchemaVersion} is not supported.", "Use schemaVersion 1.");
            if (string.IsNullOrWhiteSpace(s.MaterialId))
                Error("MATERIAL_MISSING", "$.materialId", "A stable material identity is required.", "Set materialId to the job's material.");

            int upm = s.Precision?.UnitsPerMm ?? 0;
            if (Array.IndexOf(Units.SupportedScales, upm) < 0)
            {
                Error("PRECISION_UNSUPPORTED", "$.precision.unitsPerMm", $"unitsPerMm={upm} is not supported.", "Use 1, 10, 100 or 1000 units per millimetre.");
                return; // every later conversion depends on the scale
            }

            _scale = upm;
            CheckSearch(s.Search);

            var pieces = CheckPieces(s);
            var stock = CheckStock(s);
            var offers = CheckOffers(s);
            CheckPolicy(s.MinimumReusableRemnant);

            if ((s.RollOffers?.Count ?? 0) == 0 && (s.Stock?.Count ?? 0) == 0)
                Error("NO_SOURCES", "$", "No roll offers and no warehouse stock were supplied.", "Provide at least one roll offer or one available remnant.");

            if (_errors.Count > 0) return; // feasibility checks need clean numbers

            CheckAggregates(s, pieces, stock, offers);
            if (_errors.Count > 0) return;

            CheckPieceFeasibility(s, pieces, stock, offers);

            if (!s.MaterialDirectional && pieces.Any(p => p.Req.Orientation != PatternOrientation.None))
                _warnings.Add("Material is non-directional: piece orientation requirements are ignored and all 90° rotations are permitted.");
            if (s.MinimumReusableRemnant is null)
                _warnings.Add("No reusable-remnant policy supplied (open decision, spec §12.2): every residual is reported as not eligible.");
            var currencies = (s.RollOffers ?? []).Select(o => o?.Currency ?? "").Distinct().ToList();
            if (currencies.Count > 1)
                _warnings.Add("Roll offers use different currencies; purchase-cost comparisons between them are not meaningful.");
            if ((s.RollOffers ?? []).Any(o => o is not null && string.IsNullOrWhiteSpace(o.Currency)))
                _warnings.Add("A roll offer has no currency; purchase cost is reported as a bare number.");
        }

        private void CheckSearch(SearchSettings? search)
        {
            if (search is null)
            {
                Error("SEARCH_MISSING", "$.search", "Search settings are missing.", "Supply timeBudgetMs, seed and maxCandidates.");
                return;
            }

            if (search.TimeBudgetMs < 1 || search.TimeBudgetMs > _limits.MaxTimeBudgetMs)
                Error("TIME_BUDGET_OUT_OF_RANGE", "$.search.timeBudgetMs", $"timeBudgetMs must be between 1 and {_limits.MaxTimeBudgetMs}.", "Choose a bounded search budget.");
            if (search.MaxCandidates < 1 || search.MaxCandidates > _limits.MaxCandidates)
                Error("MAX_CANDIDATES_OUT_OF_RANGE", "$.search.maxCandidates", $"maxCandidates must be between 1 and {_limits.MaxCandidates}.", "Request a smaller number of candidates.");
        }

        private bool Length(decimal value, string path, bool allowZero, out long units)
        {
            units = 0;
            if (value < 0 || (!allowZero && value == 0))
            {
                Error(allowZero ? "NEGATIVE_VALUE" : "NON_POSITIVE_DIMENSION", path, $"Value {F(value)} mm must be {(allowZero ? "non-negative" : "positive")}.", "Correct the dimension.");
                return false;
            }

            if (!Units.TryToUnits(value, _scale, out units))
            {
                Error("PRECISION_EXCEEDED", path, $"Value {value.ToString(CultureInfo.InvariantCulture)} mm has more precision than 1/{_scale} mm or is out of range; it would have to be rounded.", "Round the source data deliberately or raise precision.unitsPerMm.");
                return false;
            }

            if (units > _limits.MaxDimensionUnits)
            {
                Error("DIMENSION_LIMIT", path, $"Value {F(value)} mm exceeds the configured maximum of {Mm(_limits.MaxDimensionUnits)} mm.", "Check the units of the source data.");
                return false;
            }

            return true;
        }

        internal sealed record PieceInfo(int Index, PieceRequest Req, long FootW, long FootL, long NomW, long NomL, long T, long R, long B, long L);

        private List<PieceInfo> CheckPieces(OptimizationSnapshot s)
        {
            var result = new List<PieceInfo>();
            if (s.Pieces is null || s.Pieces.Count == 0)
            {
                Error("PIECES_EMPTY", "$.pieces", "At least one piece is required.", "Add the rectangles to cut.");
                return result;
            }

            if (s.Pieces.Count > _limits.MaxPieceTypes)
                Error("PIECE_TYPE_LIMIT", "$.pieces", $"{s.Pieces.Count} piece lines exceed the limit of {_limits.MaxPieceTypes}.", "Split the job or merge identical lines.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            long totalInstances = 0;
            for (int i = 0; i < s.Pieces.Count; i++)
            {
                string path = $"$.pieces[{i}]";
                var p = s.Pieces[i];
                if (p is null)
                {
                    Error("PIECE_NULL", path, "Piece entry is null.", "Remove the empty entry.");
                    continue;
                }

                bool ok = true;
                if (string.IsNullOrWhiteSpace(p.Id))
                {
                    Error("PIECE_ID_MISSING", path + ".id", "Piece id is required.", "Give every piece a unique id.");
                    ok = false;
                }
                else if (!ids.Add(p.Id))
                {
                    Error("PIECE_ID_DUPLICATE", path + ".id", $"Piece id '{p.Id}' is duplicated.", "Use unique piece ids; express repeats with quantity.");
                    ok = false;
                }

                if (p.Quantity < 1 || p.Quantity > _limits.MaxQuantityPerPiece)
                {
                    Error("QUANTITY_OUT_OF_RANGE", path + ".quantity", $"Quantity {p.Quantity} must be between 1 and {_limits.MaxQuantityPerPiece}.", "Correct the quantity.");
                    ok = false;
                }
                else
                {
                    totalInstances += p.Quantity;
                }

                if (!Enum.IsDefined(p.Orientation))
                {
                    Error("ORIENTATION_INVALID", path + ".orientation", "Orientation must be Vertical, Horizontal or None.", "Choose a supported orientation.");
                    ok = false;
                }

                ok &= Length(p.WidthMm, path + ".widthMm", false, out long nomW);
                ok &= Length(p.LengthMm, path + ".lengthMm", false, out long nomL);
                var pad = p.PaddingMm ?? new Padding();
                ok &= Length(pad.Top, path + ".paddingMm.top", true, out long t);
                ok &= Length(pad.Right, path + ".paddingMm.right", true, out long r);
                ok &= Length(pad.Bottom, path + ".paddingMm.bottom", true, out long b);
                ok &= Length(pad.Left, path + ".paddingMm.left", true, out long l);
                if (!ok) continue;

                long footW = nomW + l + r, footL = nomL + t + b;
                if (footW > _limits.MaxDimensionUnits || footL > _limits.MaxDimensionUnits)
                {
                    Error("DIMENSION_LIMIT", path, "Padded footprint exceeds the configured maximum dimension.", "Check dimensions and padding units.");
                    continue;
                }

                result.Add(new PieceInfo(i, p, footW, footL, nomW, nomL, t, r, b, l));
            }

            if (totalInstances > _limits.MaxPieceInstances)
                Error("INSTANCE_LIMIT", "$.pieces", $"{totalInstances} piece instances exceed the limit of {_limits.MaxPieceInstances}.", "Split the job into smaller cutting jobs.");
            return result;
        }

        internal sealed record StockInfo(int Index, StockItem Item, long W, long L);

        private List<StockInfo> CheckStock(OptimizationSnapshot s)
        {
            var result = new List<StockInfo>();
            if (s.Stock is null)
            {
                Error("STOCK_NULL", "$.stock", "Stock list is null.", "Send an empty list when no remnants are available.");
                return result;
            }

            if (s.Stock.Count > _limits.MaxStockItems)
                Error("STOCK_LIMIT", "$.stock", $"{s.Stock.Count} remnants exceed the limit of {_limits.MaxStockItems}.", "Pre-filter the candidate remnants.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < s.Stock.Count; i++)
            {
                string path = $"$.stock[{i}]";
                var st = s.Stock[i];
                if (st is null)
                {
                    Error("STOCK_NULL", path, "Stock entry is null.", "Remove the empty entry.");
                    continue;
                }

                bool ok = true;
                if (string.IsNullOrWhiteSpace(st.StockId))
                {
                    Error("STOCK_ID_MISSING", path + ".stockId", "stockId is required.", "Reference the physical remnant id.");
                    ok = false;
                }
                else if (st.StockId.StartsWith(Normalizer.NewRollPrefix, StringComparison.Ordinal))
                {
                    Error("STOCK_ID_RESERVED", path + ".stockId", $"stockId may not start with '{Normalizer.NewRollPrefix}'.", "Rename the remnant id.");
                    ok = false;
                }
                else if (!ids.Add(st.StockId))
                {
                    Error("STOCK_ID_DUPLICATE", path + ".stockId", $"Remnant '{st.StockId}' appears more than once.", "Each physical remnant may be offered once.");
                    ok = false;
                }

                if (st.MaterialId is not null && !string.Equals(st.MaterialId, s.MaterialId, StringComparison.Ordinal))
                {
                    Error("STOCK_MATERIAL_MISMATCH", path + ".materialId", $"Remnant '{st.StockId}' is material '{st.MaterialId}', job is '{s.MaterialId}'.", "Offer only remnants of the job's material.");
                    ok = false;
                }

                if (!st.Available)
                {
                    Error("STOCK_UNAVAILABLE", path + ".available", $"Remnant '{st.StockId}' is not confirmed/available (reserved, excluded or consumed).", "Remove it from the snapshot or refresh inventory.");
                    ok = false;
                }

                if (!Enum.IsDefined(st.PatternAxis))
                {
                    Error("PATTERN_AXIS_INVALID", path + ".patternAxis", "patternAxis must be Length or Width.", "Record the remnant's pattern axis.");
                    ok = false;
                }

                ok &= Length(st.WidthMm, path + ".widthMm", false, out long w);
                ok &= Length(st.LengthMm, path + ".lengthMm", false, out long len);
                if (ok) result.Add(new StockInfo(i, st, w, len));
            }

            return result;
        }

        internal sealed record OfferInfo(int Index, RollOffer Offer, long W, long? MaxLen, long? FixedLen);

        private List<OfferInfo> CheckOffers(OptimizationSnapshot s)
        {
            var result = new List<OfferInfo>();
            if (s.RollOffers is null)
            {
                Error("OFFERS_NULL", "$.rollOffers", "rollOffers is null.", "Send an empty list for warehouse-only jobs.");
                return result;
            }

            if (s.RollOffers.Count > _limits.MaxRollOffers)
                Error("OFFER_LIMIT", "$.rollOffers", $"{s.RollOffers.Count} roll offers exceed the limit of {_limits.MaxRollOffers}.", "Offer fewer roll widths.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < s.RollOffers.Count; i++)
            {
                string path = $"$.rollOffers[{i}]";
                var o = s.RollOffers[i];
                if (o is null)
                {
                    Error("OFFER_NULL", path, "Roll offer is null.", "Remove the empty entry.");
                    continue;
                }

                bool ok = true;
                if (string.IsNullOrWhiteSpace(o.OfferId) || !ids.Add(o.OfferId))
                {
                    Error("OFFER_ID_INVALID", path + ".offerId", "offerId must be present and unique.", "Give each roll offer a unique id.");
                    ok = false;
                }

                if (o.PricePerSquareMetre < 0 || o.PricePerSquareMetre > 1_000_000_000m)
                {
                    Error("PRICE_INVALID", path + ".pricePerSquareMetre", "Price must be between 0 and 1e9 per m².", "Snapshot a valid supplier price.");
                    ok = false;
                }

                if (o.MinOrderMetres < 1)
                {
                    Error("MIN_ORDER_INVALID", path + ".minOrderMetres", "minOrderMetres must be at least 1.", "Use a positive minimum order.");
                    ok = false;
                }

                if (o.OrderIncrementMetres < 1)
                {
                    Error("ORDER_INCREMENT_INVALID", path + ".orderIncrementMetres", "orderIncrementMetres must be at least 1.", "Use a positive whole-metre increment.");
                    ok = false;
                }

                if (o.MaxOrderMetres is int max && max < o.MinOrderMetres)
                {
                    Error("MAX_ORDER_INVALID", path + ".maxOrderMetres", "maxOrderMetres is below minOrderMetres.", "Correct the supplier limits.");
                    ok = false;
                }

                if (!Enum.IsDefined(o.PatternAxis))
                {
                    Error("PATTERN_AXIS_INVALID", path + ".patternAxis", "patternAxis must be Length or Width.", "Record the roll's pattern axis.");
                    ok = false;
                }

                ok &= Length(o.WidthMm, path + ".widthMm", false, out long w);
                long? fixedLen = null;
                if (o.FixedLengthMm is decimal f)
                {
                    ok &= Length(f, path + ".fixedLengthMm", false, out long fl);
                    fixedLen = fl;
                }

                long? maxLen = fixedLen;
                if (maxLen is null && o.MaxOrderMetres is int m)
                {
                    try
                    {
                        maxLen = checked(m * Units.MetreUnits(_scale));
                    }
                    catch (OverflowException)
                    {
                        Error("DIMENSION_LIMIT", path + ".maxOrderMetres", "maxOrderMetres overflows the solver's numeric range.", "Use a realistic supplier limit.");
                        ok = false;
                    }
                }

                if (ok) result.Add(new OfferInfo(i, o, w, maxLen, fixedLen));
            }

            return result;
        }

        private void CheckPolicy(RemnantPolicy? p)
        {
            if (p is null) return;
            Length(p.MinWidthMm, "$.minimumReusableRemnant.minWidthMm", true, out _);
            Length(p.MinLengthMm, "$.minimumReusableRemnant.minLengthMm", true, out _);
            if (p.MinAreaM2 is < 0)
                Error("NEGATIVE_VALUE", "$.minimumReusableRemnant.minAreaM2", "minAreaM2 must be non-negative.", "Correct the policy.");
            if (p.MaxAspectRatio is decimal a && a < 1)
                Error("ASPECT_INVALID", "$.minimumReusableRemnant.maxAspectRatio", "maxAspectRatio must be at least 1 (long side ÷ short side).", "Correct the policy.");
        }

        private void CheckAggregates(OptimizationSnapshot s, List<PieceInfo> pieces, List<StockInfo> stock, List<OfferInfo> offers)
        {
            Int128 max = _limits.MaxAggregateAreaUnits;
            Int128 req = 0, maxSideSum = 0;
            foreach (var p in pieces)
            {
                req += (Int128)p.FootW * p.FootL * p.Req.Quantity;
                maxSideSum += (Int128)Math.Max(p.FootW, p.FootL) * p.Req.Quantity;
            }

            if (req > max)
                Error("AREA_LIMIT", "$.pieces", "Total padded piece area exceeds the solver's numeric limits.", "Split the job.");

            Int128 stockArea = 0;
            foreach (var st in stock) stockArea += (Int128)st.W * st.L;
            if (stockArea > max)
                Error("AREA_LIMIT", "$.stock", "Total remnant area exceeds the solver's numeric limits.", "Offer fewer remnants.");

            foreach (var o in offers)
            {
                Int128 trial = o.MaxLen is long ml ? Int128.Min(ml, maxSideSum) : maxSideSum;
                if (o.FixedLen is long fl) trial = fl;
                if ((Int128)o.W * trial > max || trial > long.MaxValue / 4)
                    Error("AREA_LIMIT", $"$.rollOffers[{o.Index}]", "The roll search area exceeds the solver's numeric limits.", "Set maxOrderMetres or split the job.");
            }
        }

        private void CheckPieceFeasibility(OptimizationSnapshot s, List<PieceInfo> pieces, List<StockInfo> stock, List<OfferInfo> offers)
        {
            foreach (var p in pieces)
            {
                var reasons = new List<string>();
                bool fits = false;
                foreach (var o in offers)
                {
                    var oris = Normalizer.LegalOrientations(s.MaterialDirectional, p.Req.Orientation, o.Offer.PatternAxis, p.FootW, p.FootL);
                    if (oris.Any(x => x.W <= o.W && (o.MaxLen is not long ml || x.H <= ml)))
                    {
                        fits = true;
                        break;
                    }

                    reasons.Add($"roll '{o.Offer.OfferId}' ({Mm(o.W)} mm wide{(o.MaxLen is long m ? $", {Mm(m)} mm long" : "")}): {Describe(oris, o.W, o.MaxLen)}");
                }

                if (!fits)
                {
                    foreach (var st in stock)
                    {
                        var oris = Normalizer.LegalOrientations(s.MaterialDirectional, p.Req.Orientation, st.Item.PatternAxis, p.FootW, p.FootL);
                        if (oris.Any(x => x.W <= st.W && x.H <= st.L))
                        {
                            fits = true;
                            break;
                        }

                        if (reasons.Count < 6)
                            reasons.Add($"remnant '{st.Item.StockId}' ({Mm(st.W)} × {Mm(st.L)} mm): {Describe(oris, st.W, st.L)}");
                    }
                }

                if (fits) continue;

                string rotation = s.MaterialDirectional && p.Req.Orientation != PatternOrientation.None
                    ? $" It cannot be turned 90° because its grain must run {p.Req.Orientation.ToString().ToLowerInvariant()} on this directional material."
                    : "";
                bool padded = p.T + p.R + p.B + p.L > 0;
                string size = padded
                    ? $"a {Mm(p.FootW)} × {Mm(p.FootL)} mm cut-out ({Mm(p.NomW)} × {Mm(p.NomL)} mm finished + padding T{Mm(p.T)} R{Mm(p.R)} B{Mm(p.B)} L{Mm(p.L)})"
                    : $"a {Mm(p.FootW)} × {Mm(p.FootL)} mm cut-out";
                Error(
                    "PIECE_FITS_NO_SOURCE",
                    $"$.pieces[{p.Index}]",
                    $"Piece '{p.Req.Id}' needs {size}, which fits no roll or remnant.{rotation} " +
                    (reasons.Count == 0 ? "There are no roll offers or remnants to cut from." : string.Join("; ", reasons) + "."),
                    "Reduce padding, choose a wider roll offer, relax the orientation if the design allows, or supply a larger remnant.");
            }
        }

        private string Describe(Orientation[] oris, long w, long? len)
        {
            var parts = oris.Select(o =>
            {
                string which = o.Rotated ? "turned 90° it needs" : "as drawn it needs";
                if (o.W > w) return $"{which} {Mm(o.W)} mm of width";
                if (len is long l && o.H > l) return $"{which} {Mm(o.H)} mm of length";
                return $"{which} no more than is available";
            });
            return string.Join(", ", parts);
        }
    }
}

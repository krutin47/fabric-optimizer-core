using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;

namespace DreamGlass.Optimizer.Normalization;

/// <summary>Converts a validated snapshot into scaled-integer solver structures. Call only after <see cref="InputValidator"/> passes.</summary>
internal static class Normalizer
{
    public const string NewRollPrefix = "new-roll:";

    public static NormalizedProblem Normalize(OptimizationSnapshot s)
    {
        long scale = s.Precision.UnitsPerMm;
        long U(decimal mm) => Units.ToUnits(mm, scale);

        var types = new PieceType[s.Pieces.Count];
        var instances = new List<Instance>();
        long required = 0;
        for (int i = 0; i < s.Pieces.Count; i++)
        {
            var p = s.Pieces[i];
            var pad = p.PaddingMm ?? new Padding();
            long nomW = U(p.WidthMm), nomL = U(p.LengthMm);
            long t = U(pad.Top), r = U(pad.Right), b = U(pad.Bottom), l = U(pad.Left);
            long footW = nomW + l + r, footL = nomL + t + b;
            types[i] = new PieceType
            {
                Index = i,
                Request = p,
                PieceId = p.Id,
                Label = string.IsNullOrWhiteSpace(p.Label) ? p.Id : p.Label!,
                NomW = nomW,
                NomL = nomL,
                PadT = t,
                PadR = r,
                PadB = b,
                PadL = l,
                Required = p.Orientation,
                Quantity = p.Quantity,
                OnLengthAxis = LegalOrientations(s.MaterialDirectional, p.Orientation, PatternAxis.Length, footW, footL),
                OnWidthAxis = LegalOrientations(s.MaterialDirectional, p.Orientation, PatternAxis.Width, footW, footL),
            };
            for (int k = 0; k < p.Quantity; k++)
                instances.Add(new Instance { Index = instances.Count, Type = i });
            required += footW * footL * p.Quantity;
        }

        var sources = new List<SourceSpec>();
        var remnantIdx = new List<int>();
        var rollIdx = new List<int>();
        long remnantArea = 0;
        foreach (var st in s.Stock)
        {
            var spec = new SourceSpec
            {
                Index = sources.Count,
                Kind = SourceKind.WarehouseRemnant,
                SourceId = st.StockId,
                W = U(st.WidthMm),
                L = U(st.LengthMm),
                Axis = st.PatternAxis,
                Stock = st,
            };
            remnantIdx.Add(spec.Index);
            remnantArea += spec.Area;
            sources.Add(spec);
        }

        foreach (var o in s.RollOffers)
        {
            long? fixedLen = o.FixedLengthMm is decimal f ? U(f) : null;
            var spec = new SourceSpec
            {
                Index = sources.Count,
                Kind = SourceKind.NewRoll,
                SourceId = NewRollPrefix + o.OfferId,
                W = U(o.WidthMm),
                L = fixedLen ?? 0,
                Axis = o.PatternAxis,
                Offer = o,
                MetreUnits = Units.MetreUnits(scale),
                FixedLengthUnits = fixedLen,
                MinOrderMetres = o.MinOrderMetres,
                MaxOrderMetres = o.MaxOrderMetres,
                IncrementMetres = o.OrderIncrementMetres,
            };
            rollIdx.Add(spec.Index);
            sources.Add(spec);
        }

        RemnantPolicyUnits? policy = s.MinimumReusableRemnant is { } rp
            ? new RemnantPolicyUnits(U(rp.MinWidthMm), U(rp.MinLengthMm), rp.MinAreaM2, rp.MaxAspectRatio)
            : null;

        return new NormalizedProblem
        {
            Snapshot = s,
            Scale = scale,
            Directional = s.MaterialDirectional,
            Types = types,
            Instances = instances.ToArray(),
            Sources = sources.ToArray(),
            RemnantSources = remnantIdx.ToArray(),
            RollSources = rollIdx.ToArray(),
            Policy = policy,
            RequiredArea = required,
            TotalRemnantArea = remnantArea,
        };
    }

    /// <summary>
    /// Legal orientations for a piece on a source with the given pattern axis. A square footprint whose both
    /// rotations are legal is reduced to the unrotated state (redundant symmetric state, spec §4).
    /// </summary>
    internal static Orientation[] LegalOrientations(bool directional, PatternOrientation required, PatternAxis axis, long footW, long footL)
    {
        var list = new List<Orientation>(2);
        if (OrientationRules.IsAllowed(directional, required, axis, pieceRotated: false))
            list.Add(new Orientation(false, footW, footL));
        if (OrientationRules.IsAllowed(directional, required, axis, pieceRotated: true) && !(list.Count == 1 && footW == footL))
            list.Add(new Orientation(true, footL, footW));
        return list.ToArray();
    }
}

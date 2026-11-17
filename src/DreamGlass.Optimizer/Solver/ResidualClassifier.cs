using System.Globalization;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;

namespace DreamGlass.Optimizer.Solver;

/// <summary>Applies the (configurable, still-open) reusable-remnant policy to a residual rectangle.</summary>
internal static class ResidualClassifier
{
    public readonly record struct Outcome(bool Eligible, string Reason, bool Thin);

    /// <summary>Same decision as <see cref="Classify"/> without building the reason text (search hot path).</summary>
    public static (bool Eligible, bool Thin) Evaluate(long w, long h, RemnantPolicyUnits? policy, long scale)
    {
        if (policy is null) return (false, false);
        long shortSide = Math.Min(w, h), longSide = Math.Max(w, h);
        if (shortSide < policy.MinShort) return (false, true);
        if (longSide < policy.MinLong) return (false, false);
        if (policy.MinAreaM2 is decimal minArea && Units.AreaToM2(w * h, scale) < minArea) return (false, false);
        if (policy.MaxAspectRatio is decimal maxAspect && (decimal)longSide / shortSide > maxAspect) return (false, false);
        return (true, false);
    }

    public static Outcome Classify(long w, long h, RemnantPolicyUnits? policy, long scale)
    {
        if (policy is null) return new Outcome(false, "No reusable-remnant policy configured", false);
        long shortSide = Math.Min(w, h), longSide = Math.Max(w, h);
        string Mm(long u) => Units.ToMm(u, scale).ToString("0.###", CultureInfo.InvariantCulture);
        bool thin = shortSide < policy.MinShort;
        if (shortSide < policy.MinShort)
            return new Outcome(false, $"Short side {Mm(shortSide)} mm < minimum {Mm(policy.MinShort)} mm", thin);
        if (longSide < policy.MinLong)
            return new Outcome(false, $"Long side {Mm(longSide)} mm < minimum {Mm(policy.MinLong)} mm", thin);
        decimal areaM2 = Units.AreaToM2(w * h, scale);
        if (policy.MinAreaM2 is decimal minArea && areaM2 < minArea)
            return new Outcome(false, $"Area {areaM2:0.######} m² < minimum {minArea} m²", thin);
        if (policy.MaxAspectRatio is decimal maxAspect && (decimal)longSide / shortSide > maxAspect)
            return new Outcome(false, $"Aspect ratio {(decimal)longSide / shortSide:0.###} > maximum {maxAspect}", thin);
        return new Outcome(true, "Meets reusable-remnant policy", false);
    }
}

namespace DreamGlass.Optimizer.Infrastructure;

/// <summary>Solver-side conversion between decimal millimetres and scaled integer units.</summary>
internal static class Units
{
    public static readonly int[] SupportedScales = [1, 10, 100, 1000];

    /// <summary>Exact conversion; returns false when the value is not representable at this precision or overflows.</summary>
    public static bool TryToUnits(decimal mm, long unitsPerMm, out long units)
    {
        units = 0;
        decimal scaled;
        try
        {
            scaled = mm * unitsPerMm;
        }
        catch (OverflowException)
        {
            return false;
        }

        if (scaled != decimal.Truncate(scaled)) return false;
        if (scaled > long.MaxValue / 4 || scaled < long.MinValue / 4) return false;
        units = (long)scaled;
        return true;
    }

    public static long ToUnits(decimal mm, long unitsPerMm)
    {
        if (!TryToUnits(mm, unitsPerMm, out long u))
            throw new InvalidOperationException($"Value {mm} mm is not representable at 1/{unitsPerMm} mm precision.");
        return u;
    }

    public static decimal ToMm(long units, long unitsPerMm) => (decimal)units / unitsPerMm;

    /// <summary>Squared units → m² (exact: the divisor is a power of ten).</summary>
    public static decimal AreaToM2(long areaUnits, long unitsPerMm) =>
        (decimal)areaUnits / (unitsPerMm * unitsPerMm * 1_000_000m);

    public static long MetreUnits(long unitsPerMm) => 1000 * unitsPerMm;
}

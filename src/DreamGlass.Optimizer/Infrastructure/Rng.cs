namespace DreamGlass.Optimizer.Infrastructure;

/// <summary>
/// xoshiro256** seeded through SplitMix64. Implemented locally (rather than <see cref="System.Random"/>) so the
/// sequence for a given seed is fixed by this solver version, independent of the .NET runtime version.
/// </summary>
internal sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(long seed)
    {
        unchecked
        {
            ulong x = (ulong)seed;
            _s0 = SplitMix(ref x);
            _s1 = SplitMix(ref x);
            _s2 = SplitMix(ref x);
            _s3 = SplitMix(ref x);
        }
    }

    private static ulong SplitMix(ref ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public ulong NextULong()
    {
        unchecked
        {
            ulong result = RotL(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = RotL(_s3, 45);
            return result;
        }
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>Uniform integer in [0, n).</summary>
    public int Next(int n)
    {
        if (n <= 1) return 0;
        unchecked
        {
            return (int)(NextULong() % (ulong)n);
        }
    }

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public long NextLong()
    {
        unchecked
        {
            return (long)(NextULong() >> 1);
        }
    }

    public Rng Fork(long salt)
    {
        unchecked
        {
            return new Rng((long)(NextULong() ^ (ulong)salt));
        }
    }

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];
}

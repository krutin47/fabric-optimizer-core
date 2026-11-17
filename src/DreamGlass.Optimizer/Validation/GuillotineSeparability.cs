namespace DreamGlass.Optimizer.Validation;

/// <summary>
/// Decides whether a set of pairwise-disjoint rectangles inside a sheet admits <b>any</b> guillotine cut tree.
/// Useful for admitting placements proposed by non-guillotine generators (MaxRects, Skyline, CP-SAT NoOverlap2D),
/// which are only admissible once a cut tree is proven (spec §6). Greedy splitting is complete: cutting along any
/// line that crosses no rectangle preserves guillotine-separability of both halves.
/// </summary>
public static class GuillotineSeparability
{
    public readonly record struct Box(long X, long Y, long W, long H);

    public static bool IsGuillotine(IReadOnlyList<Box> boxes, long width, long length)
    {
        var stack = new Stack<(Box Region, List<Box> Items)>();
        stack.Push((new Box(0, 0, width, length), boxes.ToList()));
        while (stack.Count > 0)
        {
            var (region, items) = stack.Pop();
            if (items.Count <= 1) continue; // one rectangle can always be isolated with at most four cuts

            if (TryCut(items, b => b.X, b => b.X + b.W, region.X, region.X + region.W, out long xc))
            {
                stack.Push((new Box(region.X, region.Y, xc - region.X, region.H), items.Where(b => b.X + b.W <= xc).ToList()));
                stack.Push((new Box(xc, region.Y, region.X + region.W - xc, region.H), items.Where(b => b.X >= xc).ToList()));
                continue;
            }

            if (TryCut(items, b => b.Y, b => b.Y + b.H, region.Y, region.Y + region.H, out long yc))
            {
                stack.Push((new Box(region.X, region.Y, region.W, yc - region.Y), items.Where(b => b.Y + b.H <= yc).ToList()));
                stack.Push((new Box(region.X, yc, region.W, region.Y + region.H - yc), items.Where(b => b.Y >= yc).ToList()));
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>Finds a line strictly inside (lo, hi) that separates the items into two non-empty groups.</summary>
    private static bool TryCut(List<Box> items, Func<Box, long> start, Func<Box, long> end, long lo, long hi, out long cut)
    {
        var sorted = items.OrderBy(start).ThenBy(end).ToList();
        long reach = end(sorted[0]);
        for (int i = 1; i < sorted.Count; i++)
        {
            if (start(sorted[i]) >= reach && reach > lo && reach < hi)
            {
                cut = reach;
                return true;
            }

            reach = Math.Max(reach, end(sorted[i]));
        }

        cut = 0;
        return false;
    }
}

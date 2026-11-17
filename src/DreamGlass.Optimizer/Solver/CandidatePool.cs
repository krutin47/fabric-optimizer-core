using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Solver;

internal sealed class PoolEntry
{
    /// <summary>Metrics, identity and group, computed from the finished layout.</summary>
    public required PlanDigest Digest { get; init; }

    public required PackingState State { get; init; }

    /// <summary>
    /// The full plan, present once it has been built and has passed the independent validator. A kept layout
    /// without it has not been validated yet and is never returned as it stands.
    /// </summary>
    public CandidateCutPlan? Plan { get; set; }

    public PlanMetrics Metrics => Digest.Metrics;

    /// <summary>"warehouse-only" or "roll:{offerId}" — keeps alternative roll widths alive for diversity.</summary>
    public string Group => Digest.Group;

    public int Serial { get; set; }
}

/// <summary>The figures two plans must share to be interchangeable for the employee (the displayed trade-offs).</summary>
internal readonly record struct MetricKey(decimal Purchased, decimal Scrap, decimal LargestReusable, decimal Cost, int Cuts, decimal Stock)
{
    public static MetricKey Of(PlanMetrics m) =>
        new(m.PurchasedAreaM2, m.UnusableScrapAreaM2, m.LargestReusableAreaM2, m.PurchaseCost, m.TotalCuts, m.ExistingStockAreaConsumedM2);
}

/// <summary>
/// Bounded, always-sorted set of the best layouts found, de-duplicated by structural key.
/// <para>
/// <see cref="Admits"/> lets the search ask, from a layout's metrics alone, whether it could earn a place: the pool
/// still has room, or it beats the worst layout kept, or it is a new best for its roll offer, or it sets a new
/// extreme on one of the displayed trade-offs. Layouts that tie on every displayed figure are capped at
/// <see cref="MaxPerMetricKey"/>: the employee could not tell more of them apart.
/// </para>
/// <para>
/// Nothing leaves the pool unvalidated: <see cref="SelectFinal"/> builds the full plan of every layout it picks and
/// passes it through the independent validator; a layout that fails is dropped and the selection is repeated.
/// </para>
/// </summary>
internal sealed class CandidatePool(ObjectivePolicy policy, int capacity)
{
    public const int MaxPerMetricKey = 3;

    private readonly int _capacity = Math.Max(8, capacity);
    private readonly List<PoolEntry> _sorted = [];
    private readonly HashSet<PlanKey> _keys = [];
    private readonly Dictionary<string, PoolEntry> _groupBest = new(StringComparer.Ordinal);
    private readonly Dictionary<MetricKey, int> _perKey = [];
    private readonly IComparer<PoolEntry> _order = Comparer<PoolEntry>.Create((a, b) =>
    {
        int c = PlanRanking.Compare(a.Metrics, b.Metrics, policy);
        if (c != 0) return c;
        c = a.Digest.Key.A.CompareTo(b.Digest.Key.A);
        return c != 0 ? c : a.Digest.Key.B.CompareTo(b.Digest.Key.B);
    });

    private int _serial;

    // Extremes over the layouts currently kept; their holders survive pruning.
    private int _minCuts;
    private decimal _minCost, _minScrap, _minPurchased, _maxLargest;

    public PoolEntry? Best => _sorted.Count > 0 ? _sorted[0] : null;
    public int Count => _sorted.Count;

    /// <summary>Whether a layout with these metrics could earn a place (see the class remarks).</summary>
    public bool Admits(PlanMetrics m, string group)
    {
        if (_perKey.GetValueOrDefault(MetricKey.Of(m)) >= MaxPerMetricKey) return false;
        if (_sorted.Count < _capacity) return true;
        if (!_groupBest.TryGetValue(group, out var gb) || PlanRanking.Compare(m, gb.Metrics, policy) < 0) return true;
        if (m.TotalCuts < _minCuts || m.PurchaseCost < _minCost || m.UnusableScrapAreaM2 < _minScrap
            || m.PurchasedAreaM2 < _minPurchased || m.LargestReusableAreaM2 > _maxLargest)
            return true;
        return PlanRanking.Compare(m, _sorted[_capacity - 1].Metrics, policy) < 0;
    }

    /// <summary>Adds a layout. Returns (added, improvedBest).</summary>
    public (bool Added, bool NewBest) Add(PoolEntry e)
    {
        if (!_keys.Add(e.Digest.Key)) return (false, false);
        e.Serial = ++_serial;
        int at = _sorted.BinarySearch(e, _order);
        if (at < 0) at = ~at;
        _sorted.Insert(at, e);
        Track(e);
        if (_sorted.Count > _capacity * 2) Prune();
        return (true, at == 0);
    }

    private void Track(PoolEntry e)
    {
        var m = e.Metrics;
        var key = MetricKey.Of(m);
        _perKey[key] = _perKey.GetValueOrDefault(key) + 1;
        if (!_groupBest.TryGetValue(e.Group, out var gb) || _order.Compare(e, gb) < 0) _groupBest[e.Group] = e;
        bool first = _sorted.Count == 1 && ReferenceEquals(_sorted[0], e);
        _minCuts = first ? m.TotalCuts : Math.Min(_minCuts, m.TotalCuts);
        _minCost = first ? m.PurchaseCost : Math.Min(_minCost, m.PurchaseCost);
        _minScrap = first ? m.UnusableScrapAreaM2 : Math.Min(_minScrap, m.UnusableScrapAreaM2);
        _minPurchased = first ? m.PurchasedAreaM2 : Math.Min(_minPurchased, m.PurchasedAreaM2);
        _maxLargest = first ? m.LargestReusableAreaM2 : Math.Max(_maxLargest, m.LargestReusableAreaM2);
    }

    /// <summary>Kept layouts, best first.</summary>
    public IReadOnlyList<PoolEntry> Sorted() => _sorted;

    public IReadOnlyDictionary<string, PoolEntry> GroupBests => _groupBest;

    /// <summary>Rebuilds the derived indexes after entries were removed.</summary>
    private void Reindex()
    {
        _keys.Clear();
        _groupBest.Clear();
        _perKey.Clear();
        var kept = _sorted.ToList();
        _sorted.Clear();
        foreach (var e in kept)
        {
            _keys.Add(e.Digest.Key);
            _sorted.Add(e);
            Track(e);
        }
    }

    private void Prune()
    {
        var keep = new HashSet<PoolEntry>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _capacity; i++) keep.Add(_sorted[i]);
        foreach (var e in _groupBest.Values) keep.Add(e);

        // Keep the trade-off extremes so the final selection can still show them (ties: best ranked).
        PoolEntry? Extreme(Func<PlanMetrics, PlanMetrics, bool> better)
        {
            PoolEntry? pick = null;
            foreach (var e in _sorted)
                if (pick is null || better(e.Metrics, pick.Metrics)) pick = e;
            return pick;
        }

        foreach (var x in new[]
                 {
                     Extreme((a, b) => a.TotalCuts < b.TotalCuts),
                     Extreme((a, b) => a.PurchaseCost < b.PurchaseCost),
                     Extreme((a, b) => a.UnusableScrapAreaM2 < b.UnusableScrapAreaM2),
                     Extreme((a, b) => a.LargestReusableAreaM2 > b.LargestReusableAreaM2),
                     Extreme((a, b) => a.PurchasedAreaM2 < b.PurchasedAreaM2
                                       || (a.PurchasedAreaM2 == b.PurchasedAreaM2 && a.ExistingStockAreaConsumedM2 < b.ExistingStockAreaConsumedM2)),
                 })
        {
            if (x is not null) keep.Add(x);
        }

        _sorted.RemoveAll(e => !keep.Contains(e));
        Reindex();
    }

    /// <summary>
    /// The final selection with every picked layout built and validated by <paramref name="materialize"/> (which sets
    /// <see cref="PoolEntry.Plan"/> and returns false for a rejected plan). Rejected layouts are dropped and the
    /// selection is repeated, so the result only ever holds validated plans.
    /// </summary>
    public List<(PoolEntry Entry, List<string> Tags)> SelectFinal(int max, Func<PoolEntry, bool> materialize)
    {
        while (true)
        {
            var chosen = Select(max);
            var rejected = chosen.Where(c => !materialize(c.Entry)).Select(c => c.Entry).ToList();
            if (rejected.Count == 0) return chosen;
            _sorted.RemoveAll(e => rejected.Contains(e));
            Reindex();
        }
    }

    /// <summary>
    /// Final ranked, diverse selection (spec §5.4): the objective-best plan, then Pareto-relevant alternatives
    /// (no purchase, fewest cuts, lowest cost, largest remnant, least scrap, other roll widths), then the next
    /// best distinct plans. Every selected plan carries tags explaining why it is shown.
    /// </summary>
    private List<(PoolEntry Entry, List<string> Tags)> Select(int max)
    {
        var sorted = _sorted;
        var chosen = new List<(PoolEntry Entry, List<string> Tags)>();
        if (sorted.Count == 0) return chosen;
        var rankOf = new Dictionary<PoolEntry, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < sorted.Count; i++) rankOf[sorted[i]] = i;
        var keys = new HashSet<MetricKey>();

        void Offer(PoolEntry? e, string tag, bool requireNew = false)
        {
            if (e is null) return;
            var existing = chosen.FindIndex(c => c.Entry == e);
            if (existing >= 0)
            {
                if (!chosen[existing].Tags.Contains(tag)) chosen[existing].Tags.Add(tag);
                return;
            }

            if (requireNew && keys.Contains(MetricKey.Of(e.Metrics))) return;
            if (chosen.Count >= max) return;
            chosen.Add((e, [tag]));
            keys.Add(MetricKey.Of(e.Metrics));
        }

        PoolEntry? Best(Func<PoolEntry, decimal> key, bool descending = false, Func<PoolEntry, bool>? filter = null)
        {
            var q = sorted.Where(e => filter?.Invoke(e) ?? true);
            return (descending ? q.OrderByDescending(key) : q.OrderBy(key)).ThenBy(e => rankOf[e]).FirstOrDefault();
        }

        Offer(sorted[0], "Best by objective");
        Offer(Best(e => 0, filter: e => e.Group == PlanAssembler.WarehouseGroup), "No purchase (warehouse only)");
        Offer(Best(e => e.Metrics.ExistingStockAreaConsumedM2, filter: e => e.Metrics.PurchasedAreaM2 == sorted[0].Metrics.PurchasedAreaM2),
            "Least warehouse stock committed at the best purchase", requireNew: true);
        Offer(Best(e => e.Metrics.TotalCuts), "Fewest cuts", requireNew: true);
        Offer(Best(e => e.Metrics.PurchaseCost), "Lowest purchase cost", requireNew: true);
        Offer(Best(e => e.Metrics.LargestReusableAreaM2, descending: true), "Largest reusable remnant", requireNew: true);
        Offer(Best(e => e.Metrics.UnusableScrapAreaM2), "Least unusable scrap", requireNew: true);
        foreach (var g in _groupBest.OrderBy(kv => rankOf.GetValueOrDefault(kv.Value, int.MaxValue)))
            if (rankOf.ContainsKey(g.Value))
                Offer(g.Value, g.Key == PlanAssembler.WarehouseGroup ? "No purchase (warehouse only)" : $"Best using {g.Key.Replace("roll:", "roll offer ")}", requireNew: true);
        foreach (var e in sorted)
        {
            if (chosen.Count >= max) break;
            if (chosen.Exists(c => c.Entry == e)) continue;
            Offer(e, "Next best by objective", requireNew: true);
        }

        chosen.Sort((a, b) => rankOf[a.Entry].CompareTo(rankOf[b.Entry]));
        return chosen;
    }
}

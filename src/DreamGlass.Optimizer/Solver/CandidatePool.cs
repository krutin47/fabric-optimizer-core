using DreamGlass.Optimizer.Contracts;

namespace DreamGlass.Optimizer.Solver;

internal sealed class PoolEntry
{
    public required CandidateCutPlan Plan { get; init; }
    public required PackingState State { get; init; }

    /// <summary>"warehouse-only" or "roll:{offerId}" — keeps alternative roll widths alive for diversity.</summary>
    public required string Group { get; init; }

    public int Serial { get; set; }
}

/// <summary>Bounded set of independently validated plans, de-duplicated by candidate id.</summary>
internal sealed class CandidatePool(IComparer<CandidateCutPlan> comparer, int capacity)
{
    private readonly Dictionary<string, PoolEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PoolEntry> _groupBest = new(StringComparer.Ordinal);
    private int _serial;
    private List<PoolEntry>? _sorted;

    public PoolEntry? Best { get; private set; }
    public int Count => _byId.Count;
    public IComparer<CandidateCutPlan> Comparer => comparer;

    public bool Contains(string candidateId) => _byId.ContainsKey(candidateId);

    /// <summary>Adds a validated plan. Returns (added, improvedBest).</summary>
    public (bool Added, bool NewBest) Add(PoolEntry e)
    {
        if (_byId.ContainsKey(e.Plan.CandidateId)) return (false, false);
        e.Serial = ++_serial;
        _byId[e.Plan.CandidateId] = e;
        _sorted = null;
        if (!_groupBest.TryGetValue(e.Group, out var gb) || comparer.Compare(e.Plan, gb.Plan) < 0) _groupBest[e.Group] = e;
        bool newBest = Best is null || comparer.Compare(e.Plan, Best.Plan) < 0;
        if (newBest) Best = e;
        if (_byId.Count > Math.Max(8, capacity) * 2) Prune();
        return (true, newBest);
    }

    public IReadOnlyList<PoolEntry> Sorted()
    {
        if (_sorted is null)
        {
            _sorted = _byId.Values.ToList();
            _sorted.Sort((a, b) => comparer.Compare(a.Plan, b.Plan));
        }

        return _sorted;
    }

    public IReadOnlyDictionary<string, PoolEntry> GroupBests => _groupBest;

    private void Prune()
    {
        var sorted = Sorted();
        var keep = new Dictionary<string, PoolEntry>(StringComparer.Ordinal);
        foreach (var e in sorted.Take(capacity)) keep[e.Plan.CandidateId] = e;
        foreach (var e in _groupBest.Values) keep[e.Plan.CandidateId] = e;

        // Keep the trade-off extremes (fewest cuts, lowest cost, largest remnant) so final selection can show them.
        foreach (var pick in new Func<IEnumerable<PoolEntry>, PoolEntry?>[]
                 {
                     s => s.OrderBy(x => x.Plan.Metrics.TotalCuts).FirstOrDefault(),
                     s => s.OrderBy(x => x.Plan.Metrics.PurchaseCost).FirstOrDefault(),
                     s => s.OrderByDescending(x => x.Plan.Metrics.LargestReusableAreaM2).FirstOrDefault(),
                     s => s.OrderBy(x => x.Plan.Metrics.PurchasedAreaM2).ThenBy(x => x.Plan.Metrics.ExistingStockAreaConsumedM2).FirstOrDefault(),
                 })
        {
            if (pick(sorted) is { } x) keep[x.Plan.CandidateId] = x;
        }

        _byId.Clear();
        foreach (var kv in keep) _byId[kv.Key] = kv.Value;
        _sorted = null;
    }

    /// <summary>
    /// Final ranked, diverse selection (spec §5.4): the objective-best plan, then Pareto-relevant alternatives
    /// (no purchase, fewest cuts, lowest cost, largest remnant, least scrap, other roll widths), then the next
    /// best distinct plans. Every selected plan carries tags explaining why it is shown.
    /// </summary>
    public List<(PoolEntry Entry, List<string> Tags)> SelectFinal(int max)
    {
        var sorted = Sorted();
        var chosen = new List<(PoolEntry Entry, List<string> Tags)>();
        if (sorted.Count == 0) return chosen;
        var rankOf = new Dictionary<PoolEntry, int>();
        for (int i = 0; i < sorted.Count; i++) rankOf[sorted[i]] = i;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Offer(PoolEntry? e, string tag, bool requireNew = false)
        {
            if (e is null) return;
            var existing = chosen.FindIndex(c => c.Entry == e);
            if (existing >= 0)
            {
                if (!chosen[existing].Tags.Contains(tag)) chosen[existing].Tags.Add(tag);
                return;
            }

            if (requireNew && keys.Contains(PlanRanking.MetricKey(e.Plan.Metrics))) return;
            if (chosen.Count >= max) return;
            chosen.Add((e, [tag]));
            keys.Add(PlanRanking.MetricKey(e.Plan.Metrics));
        }

        PoolEntry? Best(Func<PoolEntry, decimal> key, bool descending = false, Func<PoolEntry, bool>? filter = null)
        {
            var q = sorted.Where(e => filter?.Invoke(e) ?? true);
            return (descending ? q.OrderByDescending(key) : q.OrderBy(key)).ThenBy(e => rankOf[e]).FirstOrDefault();
        }

        Offer(sorted[0], "Best by objective");
        Offer(Best(e => 0, filter: e => !e.Plan.RequiresPurchase), "No purchase (warehouse only)");
        Offer(Best(e => e.Plan.Metrics.ExistingStockAreaConsumedM2, filter: e => e.Plan.Metrics.PurchasedAreaM2 == sorted[0].Plan.Metrics.PurchasedAreaM2),
            "Least warehouse stock committed at the best purchase", requireNew: true);
        Offer(Best(e => e.Plan.Metrics.TotalCuts), "Fewest cuts", requireNew: true);
        Offer(Best(e => e.Plan.Metrics.PurchaseCost), "Lowest purchase cost", requireNew: true);
        Offer(Best(e => e.Plan.Metrics.LargestReusableAreaM2, descending: true), "Largest reusable remnant", requireNew: true);
        Offer(Best(e => e.Plan.Metrics.UnusableScrapAreaM2), "Least unusable scrap", requireNew: true);
        foreach (var g in _groupBest.OrderBy(kv => rankOf.GetValueOrDefault(kv.Value, int.MaxValue)))
            if (rankOf.ContainsKey(g.Value))
                Offer(g.Value, g.Key == "warehouse-only" ? "No purchase (warehouse only)" : $"Best using {g.Key.Replace("roll:", "roll offer ")}", requireNew: true);
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

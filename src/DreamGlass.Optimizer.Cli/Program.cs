using System.Diagnostics;
using System.Globalization;
using DreamGlass.Optimizer;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Printing;
using DreamGlass.Optimizer.Validation;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0)
{
    Usage();
    return 2;
}

try
{
    return args[0] switch
    {
        "optimize" => Optimize(args[1..]),
        "validate" => ValidateCmd(args[1..]),
        "bench" => Bench(args[1..]),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or IndexOutOfRangeException or FormatException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine("""
        DreamGlass optimiser CLI
          optimize <job.json> [--out result.json] [--svg <dir>] [--html <dir>] [--time <ms>] [--deterministic] [--work <units>] [--objective cheapest|historical|proposed] [--scrap-tolerance <m2>]
          validate <job.json> <result.json>
          bench [--sizes 10,50,200] [--seeds 3] [--time <ms>] [--deterministic] [--work <units>] [--csv]
        """);
    return 2;
}

static string? Opt(string[] a, string name) => Array.IndexOf(a, name) is int i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;

static OptimizationOptions ParseOptions(string[] a)
{
    var objective = Opt(a, "--objective") switch
    {
        "historical" => ObjectivePolicy.HistoricalWasteFirst,
        "proposed" => ObjectivePolicy.ProposedV1,
        _ => ObjectivePolicy.CheapestFirst,
    };
    if (Opt(a, "--scrap-tolerance") is string tol)
        objective = objective with { ScrapTieToleranceM2 = decimal.Parse(tol, System.Globalization.CultureInfo.InvariantCulture) };
    return new OptimizationOptions
    {
        Mode = a.Contains("--deterministic") ? ExecutionMode.Deterministic : ExecutionMode.TimeBudgeted,
        WorkBudget = Opt(a, "--work") is string w ? int.Parse(w) : null,
        Objective = objective,
        StagnationLimit = Opt(a, "--stagnation") is string sl ? int.Parse(sl) : OptimizationOptions.Default.StagnationLimit,
        PoolCapacity = Opt(a, "--pool") is string pc ? int.Parse(pc) : OptimizationOptions.Default.PoolCapacity,
        Exact = Opt(a, "--exact-max") is string em ? new ExactSolverOptions { MaxInstances = int.Parse(em) } : new ExactSolverOptions(),
    };
}

static int Optimize(string[] a)
{
    var job = OptimizerJson.Deserialize<OptimizationSnapshot>(File.ReadAllText(a[0]));
    if (Opt(a, "--time") is string t) job = job with { Search = job.Search with { TimeBudgetMs = int.Parse(t) } };
    var result = new HybridCutOptimizer().Optimize(job, ParseOptions(a));
    if (Opt(a, "--out") is string outPath)
    {
        File.WriteAllText(outPath, OptimizerJson.Serialize(result));
        Console.WriteLine($"result written to {outPath}");
    }

    PrintSummary(result);
    if (Opt(a, "--svg") is string dir && result.Status == RunStatus.Succeeded)
    {
        Directory.CreateDirectory(dir);
        foreach (var c in result.Candidates)
        {
            var offer = job.RollOffers?.FirstOrDefault(o => o.OfferId == c.Purchase?.RollOfferId);
            if (CutPlanSvgRenderer.RenderSupplierRollOverview(c, offer, result.SolverVersion) is string overview)
            {
                string file = Path.Combine(dir, $"rank{c.Rank}-supplier-roll.svg");
                File.WriteAllText(file, overview);
                Console.WriteLine($"  printout: {file}");
            }

            foreach (var (sourceId, svg) in CutPlanSvgRenderer.Render(c, job.MaterialDirectional, result.SolverVersion))
            {
                string file = Path.Combine(dir, $"rank{c.Rank}-{Sanitize(sourceId)}.svg");
                File.WriteAllText(file, svg);
                Console.WriteLine($"  printout: {file}");
            }
        }
    }

    if (Opt(a, "--html") is string htmlDir && result.Status == RunStatus.Succeeded)
    {
        Directory.CreateDirectory(htmlDir);
        foreach (var c in result.Candidates)
        {
            string file = Path.Combine(htmlDir, $"rank{c.Rank}-cut-plan.html");
            File.WriteAllText(file, CutPlanDocument.Render(c, job, result, DateTimeOffset.UtcNow));
            Console.WriteLine($"  cut plan: {file}");
        }
    }

    return result.Status == RunStatus.Succeeded ? 0 : 1;
}

static string Sanitize(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

static int ValidateCmd(string[] a)
{
    var job = OptimizerJson.Deserialize<OptimizationSnapshot>(File.ReadAllText(a[0]));
    var result = OptimizerJson.Deserialize<OptimizationResult>(File.ReadAllText(a[1]));
    var validator = new CutPlanValidator();
    bool all = true;
    foreach (var c in result.Candidates)
    {
        var v = validator.Validate(job, c);
        all &= v.Passed;
        Console.WriteLine($"{c.CandidateId} rank {c.Rank}: {(v.Passed ? "PASS" : "FAIL")}");
        foreach (var f in v.Failures) Console.WriteLine($"   {f.Code}: {f.Message} [{f.SourceId} {f.PieceInstanceId} {f.NodeId}]");
    }

    return all ? 0 : 1;
}

static void PrintSummary(OptimizationResult r)
{
    Console.WriteLine($"status {r.Status} ({r.StopReason}) in {r.ElapsedMs} ms · DreamGlass {HybridCutOptimizer.BuildVersion} · solver {r.SolverVersion} · objective {r.ObjectiveVersion}{(r.ObjectiveApproved ? "" : " [pending approval]")} · seed {r.Seed} · mode {r.Mode}");
    foreach (var e in r.InputErrors) Console.WriteLine($"  input error {e.Code} at {e.Path}: {e.Message}\n    → {e.Remediation}");
    var d = r.Diagnostics;
    foreach (var w in d.Warnings) Console.WriteLine($"  warning: {w}");
    foreach (var w in d.InfeasibilityReasons) Console.WriteLine($"  infeasible: {w}");
    if (d.FailureMessage is not null) Console.WriteLine($"  failure: {d.FailureMessage}");
    if (r.Status == RunStatus.InvalidInput) return;
    Console.WriteLine($"  search: {d.ConstructionsAttempted} constructions, {d.BeamRuns} beam runs, {d.ImprovementIterations} improvement iterations, {d.PlansValidated} validated / {d.PlansRejectedByValidator} rejected, pool {d.PoolSize}");
    Console.WriteLine($"  phases: {string.Join(", ", d.Phases.Select(p => $"{p.Phase} {p.ElapsedMs} ms"))}");
    if (d.Exact is { } ex) Console.WriteLine($"  exact: {(ex.Attempted ? (ex.Completed ? "completed" : "aborted") : "not attempted")} in {ex.ElapsedMs} ms, {ex.StatesExplored} states, {ex.WorkUnits} splits — {ex.Reason}");
    if (d.PurchasedAreaLowerBoundM2 is decimal lb) Console.WriteLine($"  purchased-area lower bound: {lb} m² ({d.PurchasedAreaLowerBoundMethod})");
    if (d.PurchaseCostLowerBound is decimal cl) Console.WriteLine($"  purchase-cost lower bound: {cl:0.00} ({d.PurchaseCostLowerBoundMethod})");
    if (d.DuplicateLayoutsSkipped + d.UncompetitiveLayoutsSkipped > 0)
        Console.WriteLine($"  skipped before validation: {d.DuplicateLayoutsSkipped} duplicate and {d.UncompetitiveLayoutsSkipped} uncompetitive layouts");
    foreach (var rc in d.RejectedCandidates) Console.WriteLine($"  REJECTED by the validator ({rc.Origin}): {string.Join("; ", rc.Failures.Select(f => f.Code))}");
    foreach (var c in r.Candidates)
    {
        var m = c.Metrics;
        string buy = c.Purchase is { } p
            ? $"ORDER {p.OrderedLengthMetres} m of {p.RollOfferId} ({p.OrderedWidthMm} mm): layout needs {p.ConsumedLengthMm} mm" +
              (p.PaddingLengthMm > 0 ? $" ({p.LengthWithoutPaddingMm} + {p.PaddingLengthMm} padding)" : "") +
              $", rounded up, surplus {p.RoundingSurplusMm} mm, cost {p.PurchaseCost:0.00} {p.Currency}"
            : "no purchase";
        Console.WriteLine($"  #{c.Rank} {c.CandidateId} [{string.Join(", ", c.Tags)}] {c.QualityStatus}{(c.CostBound is { } cb ? $" · price gap {cb.GapM2:0.00}{(cb.Proven ? " (proven bound)" : "")}" : "")}{(c.Bound is { } b ? $" · area gap {b.GapM2} m²" : "")}");
        Console.WriteLine($"     {CutPlanDocument.QualityText(c)}");
        Console.WriteLine($"     {buy}; stock used {m.ExistingStockAreaConsumedM2} m² in {m.WarehouseSourcesUsed} remnant(s)");
        Console.WriteLine($"     required {m.RequiredCutAreaM2} m², purchased {m.PurchasedAreaM2} m², scrap {m.UnusableScrapAreaM2} m², reusable {m.ReusableResidualAreaM2} m² (largest {m.LargestReusableAreaM2} m²), cuts {m.TotalCuts}, utilisation {m.MaterialUtilization:P2}, validation {(c.Validation.Passed ? "PASS" : "FAIL")}");
    }
}

static int Bench(string[] a)
{
    var sizes = (Opt(a, "--sizes") ?? "10,50,200").Split(',').Select(int.Parse).ToArray();
    int seeds = int.Parse(Opt(a, "--seeds") ?? "3");
    int time = int.Parse(Opt(a, "--time") ?? "5000");
    var options = ParseOptions(a);
    var optimizer = new HybridCutOptimizer();
    if (a.Contains("--csv")) return BenchCsv(sizes, seeds, time, options, optimizer);
    Console.WriteLine("instances | seed | status    | ms     | cost      | purchased m² | LB m²    | used mm  | stock m² | scrap m² | keep m² | cuts | built  | dup/weak   | valid/rej | improve | build/assemble/validate ms | exact");
    foreach (int n in sizes)
        for (int seed = 1; seed <= seeds; seed++)
        {
            var job = SyntheticJobs.Mixed(n, seed) with { Search = new SearchSettings { TimeBudgetMs = time, Seed = seed, MaxCandidates = 3 } };
            var sw = Stopwatch.StartNew();
            var r = optimizer.Optimize(job, options);
            sw.Stop();
            var best = r.Candidates.FirstOrDefault();
            var m = best?.Metrics;
            var d = r.Diagnostics;
            string exact = d.Exact is { Attempted: true } e ? (e.Completed ? $"proved {e.ElapsedMs} ms" : $"aborted {e.ElapsedMs} ms") : "-";
            Console.WriteLine($"{n,9} | {seed,4} | {r.Status,-9} | {sw.ElapsedMilliseconds,6} | {m?.PurchaseCost,9:0.00} | {m?.PurchasedAreaM2,12:0.000} | {d.PurchasedAreaLowerBoundM2,8:0.000} | {m?.ConsumedLengthMm,8:0} | {m?.ExistingStockAreaConsumedM2,8:0.000} | {m?.UnusableScrapAreaM2,8:0.000} | {m?.LargestReusableAreaM2,7:0.000} | {m?.TotalCuts,4} | {d.CompletePlansBuilt,6} | {d.DuplicateLayoutsSkipped,4}/{d.UncompetitiveLayoutsSkipped,-5} | {d.PlansValidated,5}/{d.PlansRejectedByValidator,-3} | {d.ImprovementIterations,7} | {d.ConstructionMs,5}/{d.AssemblyMs,5}/{d.ValidationMs,5} | {exact}");
        }

    return 0;
}

/// <summary>Machine-readable benchmark rows, for comparing two builds on the same jobs (see docs/testing).</summary>
static int BenchCsv(int[] sizes, int seeds, int time, OptimizationOptions options, HybridCutOptimizer optimizer)
{
    Console.WriteLine("instances,seed,status,ms,cost,scrap_m2,largest_keep_m2,used_mm,cuts,purchased_m2,stock_m2,cost_lb,area_lb_m2,exact");
    foreach (int n in sizes)
        for (int seed = 1; seed <= seeds; seed++)
        {
            var job = SyntheticJobs.Mixed(n, seed) with { Search = new SearchSettings { TimeBudgetMs = time, Seed = seed, MaxCandidates = 3 } };
            var sw = Stopwatch.StartNew();
            var r = optimizer.Optimize(job, options);
            sw.Stop();
            var m = r.Candidates.FirstOrDefault()?.Metrics;
            var d = r.Diagnostics;
            string exact = d.Exact is { Attempted: true } e ? (e.Completed ? "proved" : "aborted") : "";
            Console.WriteLine(string.Join(',', n, seed, r.Status, sw.ElapsedMilliseconds, m?.PurchaseCost, m?.UnusableScrapAreaM2, m?.LargestReusableAreaM2, m?.ConsumedLengthMm ?? 0, m?.TotalCuts, m?.PurchasedAreaM2, m?.ExistingStockAreaConsumedM2, d.PurchaseCostLowerBound, d.PurchasedAreaLowerBoundM2, exact));
        }

    return 0;
}

/// <summary>Seeded synthetic workloads for benchmarking (not DreamGlass production data).</summary>
internal static class SyntheticJobs
{
    public static OptimizationSnapshot Mixed(int instances, int seed)
    {
        var rng = new Random(seed * 7919 + instances);
        var pieces = new List<PieceRequest>();
        int left = instances, id = 0;
        while (left > 0)
        {
            int q = Math.Min(left, 1 + rng.Next(4));
            left -= q;
            var orientation = (PatternOrientation)rng.Next(3);
            pieces.Add(new PieceRequest
            {
                Id = $"P{++id}",
                WidthMm = 200 + 10 * rng.Next(100),
                LengthMm = 300 + 10 * rng.Next(150),
                Quantity = q,
                Orientation = orientation,
                PaddingMm = rng.Next(3) == 0 ? new Padding { Top = 5, Right = 5, Bottom = 5, Left = 5 } : new Padding(),
            });
        }

        var stock = Enumerable.Range(1, 2 + rng.Next(5)).Select(i => new StockItem
        {
            StockId = $"R{i}",
            WidthMm = 400 + 50 * rng.Next(40),
            LengthMm = 600 + 50 * rng.Next(60),
            PatternAxis = rng.Next(2) == 0 ? PatternAxis.Length : PatternAxis.Width,
            Version = 1,
        }).ToList();

        return new OptimizationSnapshot
        {
            JobRevisionId = $"bench-{instances}-{seed}",
            MaterialId = "fabric-bench",
            MaterialDirectional = true,
            Pieces = pieces,
            RollOffers =
            [
                new RollOffer { OfferId = "roll-2800", WidthMm = 2800, PricePerSquareMetre = 12.50m, Currency = "EUR" },
                new RollOffer { OfferId = "roll-1400", WidthMm = 1400, PricePerSquareMetre = 13.10m, Currency = "EUR" },
            ],
            Stock = stock,
            MinimumReusableRemnant = new RemnantPolicy { MinWidthMm = 200, MinLengthMm = 300 },
        };
    }
}

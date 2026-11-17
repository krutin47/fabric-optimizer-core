using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamGlass.Optimizer;
using DreamGlass.Optimizer.Contracts;
using DreamGlass.Optimizer.Infrastructure;
using DreamGlass.Optimizer.Normalization;
using DreamGlass.Optimizer.Printing;
using DreamGlass.Optimizer.Validation;

// Local playground for trying the optimiser by hand. Not a production API: no user accounts, no persistence.
//
// Runs the same way from source (`dotnet run`) and from the download-and-run package (scripts/package-apps.sh):
// - The page files are looked up next to the program when they are there (packaged app), so it works whatever folder
//   it was started from (double-click in Explorer or Finder).
// - On a hosting service (Azure App Service, a container platform) it listens on the port the host assigns (PORT,
//   WEBSITES_PORT, or 8080 on Azure App Service) and opens no browser. It then asks for a shared password
//   (DREAMGLASS_PASSWORD, see PasswordGate) and refuses to run open without one; the host provides HTTPS.
// - Otherwise, without --urls it listens on this computer only (localhost), on the first free port from 5080, and opens
//   the browser. Pass --no-browser to skip that, or --urls http://0.0.0.0:5080 to reach it from other machines.
// Every response carries browser security headers, and a request body is limited to 1 MB.
var noBrowser = args.Contains("--no-browser");
args = args.Where(a => a != "--no-browser").ToArray();
var packagedRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = packagedRoot });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = Hosting.MaxRequestBytes;
    k.AddServerHeader = false;
});
var hostPort = Hosting.HostPort();
var gate = PasswordGate.FromEnvironment(hosted: hostPort is not null);
string? autoUrl = null;
if (!Hosting.HasConfiguredUrls(builder.Configuration))
{
    if (hostPort is int port)
    {
        builder.WebHost.UseUrls($"http://+:{port}");
    }
    else
    {
        autoUrl = $"http://localhost:{LocalPorts.FirstFree(5080, 5099)}";
        builder.WebHost.UseUrls(autoUrl);
    }
}

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = autoUrl ?? app.Urls.FirstOrDefault() ?? "";
    Console.WriteLine();
    Console.WriteLine($"  DreamGlass playground {HybridCutOptimizer.BuildVersion} (solver {HybridCutOptimizer.SolverVersion})");
    Console.WriteLine($"  Open {url} in your browser.");
    Console.WriteLine($"  Password: {gate.Describe()}");
    Console.WriteLine("  Keep this window open while you use it. Close it (or press Ctrl+C) to stop.");
    Console.WriteLine();
    if (autoUrl is not null && !noBrowser)
        Browser.TryOpen(autoUrl);
});
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h.ContentSecurityPolicy = Hosting.ContentSecurityPolicy;
    // same-origin: the page's own requests keep their Referer and Origin (a login layer in front may check them on
    // POST); links to other sites get no referrer.
    h["Referrer-Policy"] = "same-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // Browsers only honour this over HTTPS, which the host provides; plain-http local runs are unaffected.
    if (hostPort is not null)
        h.StrictTransportSecurity = "max-age=31536000";
    await next();
});
app.Use(gate.InvokeAsync);
app.UseDefaultFiles();
// Always revalidate (cheap 304 when unchanged), so after an update the browser never mixes an old cached page with a
// new script.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

const int MaxPlaygroundBudgetMs = 30_000;

app.MapGet("/api/info", () => Results.Text(OptimizerJson.Serialize(new
{
    buildVersion = HybridCutOptimizer.BuildVersion,
    solverVersion = HybridCutOptimizer.SolverVersion,
    validatorVersion = CutPlanValidator.Version,
    objectives = new[] { ObjectivePolicy.CheapestFirst.Version, ObjectivePolicy.HistoricalWasteFirst.Version, ObjectivePolicy.ProposedV1.Version },
    maxBudgetMs = MaxPlaygroundBudgetMs,
}, indented: false), "application/json"));

// Form-side pre-check: runs only the optimiser's own input validator (no search), so the form can flag a piece that
// fits no roll or remnant before the user presses Optimize. Same rules and messages as a real run.
app.MapPost("/api/check", async (HttpContext ctx) =>
{
    var (snapshot, readError) = await Api.ReadJsonAsync<OptimizationSnapshot>(ctx);
    if (readError is not null)
        return readError;

    if (snapshot?.Search is { TimeBudgetMs: > MaxPlaygroundBudgetMs })
        snapshot = snapshot with { Search = snapshot.Search with { TimeBudgetMs = MaxPlaygroundBudgetMs } };
    var outcome = InputValidator.Validate(snapshot);
    return Results.Text(OptimizerJson.Serialize(new { errors = outcome.Errors, warnings = outcome.Warnings }, indented: false), "application/json");
});

app.MapPost("/api/optimize", async (HttpContext ctx) =>
{
    var (req, readError) = await Api.ReadJsonAsync<PlaygroundRequest>(ctx);
    if (readError is not null)
        return readError;
    if (req?.Snapshot is null)
        return Api.Error("Request must contain a 'snapshot'.", StatusCodes.Status400BadRequest);

    // Keep the playground responsive: cap the budget but leave invalid values for the validator to report.
    var snapshot = req.Snapshot;
    if (snapshot.Search is { TimeBudgetMs: > MaxPlaygroundBudgetMs })
        snapshot = snapshot with { Search = snapshot.Search with { TimeBudgetMs = MaxPlaygroundBudgetMs } };

    var options = new OptimizationOptions
    {
        Mode = req.Deterministic ? ExecutionMode.Deterministic : ExecutionMode.TimeBudgeted,
        WorkBudget = req.Deterministic ? req.WorkBudget : null,
        Objective = Objectives.Pick(req.Objective, req.ScrapToleranceM2),
    };

    var result = await new HybridCutOptimizer().OptimizeAsync(snapshot, options, ctx.RequestAborted);
    var printouts = result.Candidates.ToDictionary(
        c => c.CandidateId,
        c => CutPlanSvgRenderer.Render(c, snapshot.MaterialDirectional, result.SolverVersion).Select(p => new { sourceId = p.SourceId, svg = p.Svg }).ToList());
    // Full supplier-roll view per plan (null when the plan orders nothing or the roll length is unknown).
    var overviews = result.Candidates.ToDictionary(
        c => c.CandidateId,
        c => CutPlanSvgRenderer.RenderSupplierRollOverview(c, snapshot.RollOffers?.FirstOrDefault(o => o.OfferId == c.Purchase?.RollOfferId), result.SolverVersion));
    return Results.Text(OptimizerJson.Serialize(new { result, printouts, overviews, effectiveSnapshot = snapshot }, indented: false), "application/json");
});

try
{
    app.Run();
}
catch (Exception ex)
{
    // A double-clicked console window closes as soon as the program exits; keep it open so the reason can be read.
    Console.Error.WriteLine($"The playground could not start: {ex.Message}");
    // Only on your own computer: on a hosting service, waiting would stop the host from restarting it.
    if (autoUrl is not null && !Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Press Enter to close this window.");
        Console.ReadLine();
    }

    return 1;
}

return 0;

internal static class Hosting
{
    /// <summary>Largest request body accepted. A big job is well under 100 KB.</summary>
    public const long MaxRequestBytes = 1_000_000;

    /// <summary>
    /// The page loads only its own script, styles and data. Inline style attributes are used by the page and the SVG
    /// printouts; blob: images are the "open full size" view.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Addresses set by --urls, ASPNETCORE_URLS or ASPNETCORE_HTTP_PORTS / HTTPS_PORTS (the .NET container default).</summary>
    public static bool HasConfiguredUrls(IConfiguration config) =>
        !string.IsNullOrEmpty(config["urls"]) || !string.IsNullOrEmpty(config["http_ports"]) || !string.IsNullOrEmpty(config["https_ports"])
        || Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is not null;

    /// <summary>
    /// The port a hosting service assigns: PORT (container platforms), WEBSITES_PORT, or 8080 on Azure App Service
    /// (which sets WEBSITE_SITE_NAME). Null on your own computer.
    /// </summary>
    public static int? HostPort()
    {
        foreach (var name in new[] { "PORT", "WEBSITES_PORT" })
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(name), out var port) && port is > 0 and < 65536)
                return port;
        }

        return Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null ? 8080 : null;
    }
}

/// <summary>
/// Shared password for a hosted playground, checked with HTTP Basic: the browser asks once per session and sends it
/// with every request, so the page and its API calls need nothing more. Set DREAMGLASS_PASSWORD (at least
/// <see cref="MinLength"/> characters); any user name is accepted. On a hosting service the playground does not run
/// open: without a password it answers every request with how to set one, unless DREAMGLASS_ALLOW_PUBLIC=true (for
/// when a login of your own sits in front). On your own computer it stays open unless a password is set.
/// </summary>
internal sealed class PasswordGate
{
    public const int MinLength = 12;
    private const string Realm = "DreamGlass playground";

    private readonly byte[]? _hash; // SHA-256 of the password: compared in constant time, never logged
    private readonly string? _problem;

    private PasswordGate(byte[]? hash, string? problem) => (_hash, _problem) = (hash, problem);

    public static PasswordGate FromEnvironment(bool hosted)
    {
        var password = Environment.GetEnvironmentVariable("DREAMGLASS_PASSWORD");
        if (!string.IsNullOrEmpty(password))
        {
            return password.Length < MinLength
                ? new(null, $"The DREAMGLASS_PASSWORD setting is too short: use at least {MinLength} characters, then restart the app.")
                : new(SHA256.HashData(Encoding.UTF8.GetBytes(password)), null);
        }

        bool allowPublic = string.Equals(Environment.GetEnvironmentVariable("DREAMGLASS_ALLOW_PUBLIC"), "true", StringComparison.OrdinalIgnoreCase);
        return hosted && !allowPublic
            ? new(null, $"This playground has no password yet. Add the app setting DREAMGLASS_PASSWORD (at least {MinLength} characters) and restart the app.")
            : new(null, null);
    }

    public string Describe() =>
        _problem is not null ? $"NOT SET UP. {_problem}"
        : _hash is not null ? "required (DREAMGLASS_PASSWORD)"
        : "none (open)";

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (_problem is not null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(_problem);
            return;
        }

        if (_hash is null || IsAuthorized(ctx.Request.Headers.Authorization.ToString()))
        {
            await next(ctx);
            return;
        }

        // A wrong password waits a second before the answer, so guessing is slow.
        if (ctx.Request.Headers.Authorization.Count > 0)
            await Task.Delay(TimeSpan.FromSeconds(1), ctx.RequestAborted);
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Password required. Reload the page and enter the playground password.");
    }

    private bool IsAuthorized(string header)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        int colon = decoded.IndexOf(':');
        if (colon < 0)
            return false;
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(decoded[(colon + 1)..]));
        return CryptographicOperations.FixedTimeEquals(given, _hash);
    }
}

internal static class Api
{
    /// <summary>Reads the request body as JSON; a bad or oversized body becomes a JSON error the page shows.</summary>
    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext ctx)
    {
        try
        {
            return (await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, OptimizerJson.Options, ctx.RequestAborted), null);
        }
        catch (JsonException ex)
        {
            return (default, Error($"Could not read the job JSON: {ex.Message}", StatusCodes.Status400BadRequest));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (default, Error($"The job is too large: the playground accepts up to {Hosting.MaxRequestBytes / 1_000_000} MB per request.", ex.StatusCode));
        }
    }

    public static IResult Error(string message, int statusCode) =>
        Results.Text(OptimizerJson.Serialize(new { error = message }, false), "application/json", statusCode: statusCode);
}

internal static class LocalPorts
{
    /// <summary>First port in [from, to] that nothing on this computer listens on; <paramref name="from"/> if all are taken.</summary>
    public static int FirstFree(int from, int to)
    {
        for (var port = from; port <= to; port++)
        {
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Taken (for example a second copy of the playground); try the next one.
            }
        }

        return from;
    }
}

internal static class Browser
{
    /// <summary>Opens the URL in the default browser. Best effort: on a machine without one the console message is enough.</summary>
    public static void TryOpen(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch (Exception)
        {
            // No browser available (server, container): the address is printed above.
        }
    }
}

internal sealed record PlaygroundRequest
{
    public OptimizationSnapshot? Snapshot { get; init; }
    public bool Deterministic { get; init; }
    public int? WorkBudget { get; init; }
    public string? Objective { get; init; }

    /// <summary>Scrap tie tolerance (m²) for "cheapest"; null keeps the policy's 0.01 m².</summary>
    public decimal? ScrapToleranceM2 { get; init; }
}

internal static class Objectives
{
    /// <summary>"cheapest" (default, approved), "historical" (least leftover first) or "proposed" (spec §5 proposal, API only).</summary>
    public static ObjectivePolicy Pick(string? name, decimal? scrapToleranceM2) => name switch
    {
        "historical" => ObjectivePolicy.HistoricalWasteFirst,
        "proposed" => ObjectivePolicy.ProposedV1,
        _ => scrapToleranceM2 is >= 0 and var tol
            ? ObjectivePolicy.CheapestFirst with { ScrapTieToleranceM2 = tol }
            : ObjectivePolicy.CheapestFirst,
    };
}

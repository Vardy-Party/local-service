using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging.EventLog;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using VardyParty.LocalService;
using VardyParty.LocalService.Auth;
using VardyParty.LocalService.Client.Discovery;
using VardyParty.LocalService.Compute;
using VardyParty.LocalService.Discovery;
using VardyParty.LocalService.ScrapeHealth;

var builder = WebApplication.CreateBuilder(args);

InsertLocalAppSettings(builder);

// Always allow local Auth0 secrets (dotnet user-secrets), including when
// ASPNETCORE_ENVIRONMENT is not Development (e.g. --no-launch-profile).
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.None);

// WebApplicationFactory / CI host tests set ASPNETCORE_ENVIRONMENT=Testing and skip LAN bind,
// discovery, and browser install so endpoints can be exercised without live dependencies.
var skipLanHost = builder.Environment.IsEnvironment("Testing")
    || builder.Configuration.GetValue("Testing:SkipLanHost", false);

var loopbackAuthBypass = LoopbackAuthBypassHandler.IsEnabled();
var selectedHttpPort = 0;
if (!skipLanHost)
{
    selectedHttpPort = DiscoveryProtocol.HttpPorts
        .Where(port => port > 0)
        .Distinct()
        .FirstOrDefault(port => IsHttpPortAvailable(port));

    if (selectedHttpPort == 0)
    {
        throw new InvalidOperationException(
            $"No available HTTP port found in configured range: {string.Join(", ", DiscoveryProtocol.HttpPorts)}");
    }

    if (loopbackAuthBypass)
    {
        // Probe bypass must not listen on LAN — pair with LoopbackAuthBypassHandler.
        builder.WebHost.UseUrls($"http://127.0.0.1:{selectedHttpPort}");
    }
    else
    {
        // Explicitly bind to all interfaces (0.0.0.0) for LAN discoverability as a service.
        builder.WebHost.UseUrls($"http://0.0.0.0:{selectedHttpPort}");
    }
}

builder.Services.AddSingleton(new DiscoveryRuntimeState { HttpPort = selectedHttpPort });
builder.Services.AddSingleton<ComputeHostAgent>();
// Fold SCRAPE_HEALTH_ALLOWED_API_HOSTS CSV into IConfiguration before allowlist bind
// (from-source / systemd); FromConfiguration stays ambient-env-free for unit tests.
ScrapeHealthApiBaseAllowlist.ApplyCsvEnvironmentToConfiguration(builder.Configuration);
builder.Services.AddSingleton(ScrapeHealthApiBaseAllowlist.FromConfiguration(builder.Configuration));
builder.Services.AddHttpClient<ScrapeHealthReporter>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Allowlist gates the requested base only; never follow 3xx off-host with Bearer/telemetry.
    AllowAutoRedirect = false
});
var mpScrapeGate = MpScrapeGate.FromEnvironment();
builder.Services.AddSingleton(mpScrapeGate);
var hostSharePath = builder.Configuration["Compute:HostSharePath"];
if (string.IsNullOrWhiteSpace(hostSharePath))
{
    hostSharePath = skipLanHost
        ? Path.Combine(Path.GetTempPath(), "vardy-host-share-test", "host-share.dat")
        : HostShareStore.DefaultFilePath();
}

if (skipLanHost)
{
    builder.Services.AddDataProtection()
        .SetApplicationName("VardyParty.LocalService.Tests")
        .UseEphemeralDataProtectionProvider();
}
else
{
    var shareDirectory = Path.GetDirectoryName(Path.GetFullPath(hostSharePath))
        ?? HostShareStore.DefaultKeyDirectory();
    var keysDirectory = Path.Combine(shareDirectory, "keys");
    Directory.CreateDirectory(shareDirectory);
    Directory.CreateDirectory(keysDirectory);
    HostShareAcl.RestrictToCurrentUser(shareDirectory);
    HostShareAcl.RestrictToCurrentUser(keysDirectory);
    var dataProtection = builder.Services.AddDataProtection()
        .SetApplicationName("VardyParty.LocalService")
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    if (OperatingSystem.IsWindows())
    {
        dataProtection.ProtectKeysWithDpapi();
    }
}

builder.Services.AddSingleton(sp =>
{
    var protector = new DataProtectionHostShareProtector(
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector(DataProtectionHostShareProtector.Purpose));
    return new HostShareStore(hostSharePath, protector);
});

// Enable native service host integration where available.
if (!skipLanHost && OperatingSystem.IsWindows())
{
    ConfigureWindowsHost(builder);
}

if (!skipLanHost && OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

// ── JSON serialisation: camelCase to match the existing Worker API contract ──
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.WriteIndented = false;
});

// ── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length > 0)
            policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader();
        else
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// ── Auth0 JWT (Client already sends Bearer via Auth0ApiTokenHandler) ───────────
// /health stays anonymous for LAN discovery; /play and /mp require stream-viewer.
var auth0Settings = LocalServiceAuthExtensions.ResolveAuth0Settings(builder.Configuration);
builder.Services.AddLocalServiceAuth(builder.Environment, builder.Configuration, auth0Settings);

if (!skipLanHost)
{
    // ── Application services ─────────────────────────────────────────────────────
    StrategyPlugins.Register(builder.Services, builder.Configuration);

    // ── LAN discovery ─────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<FirewallRuleService>();
    builder.Services.AddSingleton<IFirewallRuleService>(sp => sp.GetRequiredService<FirewallRuleService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FirewallRuleService>());
    builder.Services.AddHostedService<UdpDiscoveryService>();
    builder.Services.AddHostedService<MdnsAdvertisementService>();

    // ── Playwright first-run browser install ─────────────────────────────────────
    var configuredBrowsersPath = builder.Configuration["Playwright:BrowsersPath"] ?? "./browsers";
    var browsersPath = configuredBrowsersPath;

    if (!Path.IsPathRooted(browsersPath))
    {
        var relativePath = configuredBrowsersPath.TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string? writableRoot = null;

        if (OperatingSystem.IsLinux())
        {
            writableRoot = Environment.GetEnvironmentVariable("SNAP_COMMON");
            if (string.IsNullOrWhiteSpace(writableRoot))
                writableRoot = Environment.GetEnvironmentVariable("SNAP_DATA");
            if (string.IsNullOrWhiteSpace(writableRoot))
                writableRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        }

        if (string.IsNullOrWhiteSpace(writableRoot))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            writableRoot = string.IsNullOrWhiteSpace(localAppData)
                ? AppContext.BaseDirectory
                : Path.Combine(localAppData, "VardyParty.LocalService");
        }

        browsersPath = Path.Combine(writableRoot, string.IsNullOrWhiteSpace(relativePath) ? "browsers" : relativePath);
    }

    browsersPath = Path.GetFullPath(browsersPath);
    Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);

    if (!Directory.Exists(browsersPath) || !Directory.EnumerateDirectories(browsersPath).Any())
    {
        Console.WriteLine($"Playwright Chromium not found at '{browsersPath}' — running first-time install (requires internet access)...");

        try
        {
            Directory.CreateDirectory(browsersPath);
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                Console.Error.WriteLine($"Playwright Chromium install failed with exit code {exitCode}. Service discovery will continue, but /play requests may fail until browsers are installed.");
            }
            else
            {
                Console.WriteLine("Playwright Chromium installed.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Playwright Chromium install failed: {ex.Message}. Service discovery will continue, but /play requests may fail until browsers are installed.");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
if (loopbackAuthBypass)
{
    logger.LogWarning(
        "VARDYPARTY_LOOPBACK_AUTH_BYPASS=1: JWT skipped for loopback clients only; " +
        "HTTP bind forced to 127.0.0.1 (never enable on a shared LAN host).");
}
else
{
    logger.LogInformation(
        "Auth0 JWT enabled for Domain={Domain} Audience={Audience} Role={Role} RoleClaimType={RoleClaimType}",
        auth0Settings.NormalizedDomain,
        string.IsNullOrWhiteSpace(auth0Settings.Audience) ? "(empty)" : "(set)",
        auth0Settings.RequiredRole,
        string.IsNullOrWhiteSpace(auth0Settings.RequiredRoleClaimType) ? "(default)" : auth0Settings.RequiredRoleClaimType);
}

app.UseCors();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/mp", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/play", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogInformation(
            "Incoming {Method} {Path} from {RemoteIp} authorization={HasAuthorization}",
            context.Request.Method,
            path,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            context.Request.Headers.Authorization.Count > 0);
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();
var runtimeState = app.Services.GetRequiredService<DiscoveryRuntimeState>();
var configuredUdpPorts = DiscoveryProtocol.DiscoveryPorts
    .Where(port => port > 0)
    .Distinct()
    .ToArray();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses
        .ToArray() ?? [];

    logger.LogInformation("✓ HTTP listening on: {Addresses}", addresses.Length > 0 ? string.Join(", ", addresses) : "unknown");
    logger.LogInformation("✓ Discovery configured: UDP [{UdpPorts}], mDNS {ServiceType}, HTTP ports [{HttpPorts}], active port {HttpPort}",
        string.Join(", ", configuredUdpPorts),
        DiscoveryProtocol.ServiceType,
        string.Join(", ", DiscoveryProtocol.HttpPorts),
        runtimeState.HttpPort);
    logger.LogInformation("ℹ Firewall rules will be created for both 'Private' and 'Public' network profiles to ensure LAN discovery.");
    logger.LogInformation("ℹ If clients still cannot discover this service, check: (1) Firewall rules exist in Windows Defender, (2) Network is not in 'Public' profile, (3) UDP/TCP ports are not blocked by corporate policies.");
    if (!skipLanHost)
    {
        _ = ResumeSavedHostShareAsync(app.Services, lifetime.ApplicationStopping);
    }
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = LocalServiceHostVersion.Current,
    capabilities = mpScrapeGate.FilterCapabilities(DiscoveryProtocol.ServiceCapabilities)
})).AllowAnonymous();

// GET /play/{*url}?stream={name}
// The catch-all {*url} handles encoded URLs containing colons or encoded slashes.
// Clients should encodeURIComponent the target URL before appending it to the path.
// Optional query: stream — select a named stream on multi-stream V1/FB pages.
// When stream is omitted on a multi-stream page, the first listed stream is resolved.
app.MapGet("/play/{*url}", async (string url, string? stream, HttpContext httpContext, ScrapeHealthReporter scrapeHealth, ScrapeHealthApiBaseAllowlist scrapeHealthAllowlist, ILogger<Program> requestLogger, CancellationToken ct) =>
{
    var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var correlationId = CorrelationId(httpContext.Request);
    var targetUrl = Uri.UnescapeDataString(url ?? "").Trim();
    var streamName = string.IsNullOrWhiteSpace(stream) ? null : stream.Trim();
    var scrapeContext = ScrapeHealthContextReader.TryRead(httpContext.Request, scrapeHealthAllowlist);

    if (string.IsNullOrEmpty(targetUrl))
    {
        requestLogger.LogWarning("[{CorrelationId}] /play request rejected from {RemoteIp}: url parameter is required", correlationId, remoteIp);
        return Results.BadRequest(new { error = "url parameter is required" });
    }

    if (!PopProxyHeader.TryRead(httpContext.Connection.RemoteIpAddress, httpContext.Request.Headers[PopProxyHeader.Name], out var popProxy, out var popError))
    {
        requestLogger.LogWarning("[{CorrelationId}] /play request rejected from {RemoteIp}: {Error}", correlationId, remoteIp, popError);
        return Results.BadRequest(new { error = popError });
    }

    if (!PlayDns.TryReadTemplate(httpContext.Request, out var dohTemplate, out var dohError))
    {
        requestLogger.LogWarning("[{CorrelationId}] /play request rejected from {RemoteIp}: {Error}", correlationId, remoteIp, dohError);
        return Results.BadRequest(new { error = dohError });
    }

    dohTemplate = PlayDns.SuppressWhenPopProxy(dohTemplate, popProxy);
    var connectionId = ClientConnectionId(httpContext.Request);

    var scrape = StrategyPlugins.Find(httpContext.RequestServices, "VardyParty.LocalService.V1.Scrape", "VardyParty.LocalService.V1.Scrape.V1ScrapePlugin");
    if (scrape is null)
    {
        requestLogger.LogError("[{CorrelationId}] /play rejected from {RemoteIp}: V1 scrape plugin not registered", correlationId, remoteIp);
        return Results.Json(new { error = "v1 scrape plugin not registered" },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    requestLogger.LogInformation(
        "[{CorrelationId}] /play request from {RemoteIp} targeting {TargetUrl}{StreamSuffix} doh={Doh} pop={Pop} connection={Connection}",
        correlationId,
        remoteIp,
        targetUrl,
        streamName is null ? "" : $" (stream={streamName})",
        PlayDns.DohFlag(dohTemplate),
        PlayDns.PopFlag(popProxy),
        connectionId ?? "(none)");

    try
    {
        var result = await StrategyPlugins.ResolveV1Async(scrape, targetUrl, streamName, dohTemplate, popProxy, connectionId, ct);
        var statusCode = StrategyPlugins.StatusCodeOf(result);
        requestLogger.LogInformation("[{CorrelationId}] /play response to {RemoteIp}: status {StatusCode}", correlationId, remoteIp, statusCode);
        scrapeHealth.TryReport(
            scrapeContext,
            targetUrl,
            streamName,
            "v1",
            ok: !string.IsNullOrWhiteSpace(StrategyPlugins.Text(result, "Url")),
            error: StrategyPlugins.Text(result, "Error"),
            httpStatus: statusCode,
            durationMs: StrategyPlugins.TimingsTotal(result));
        return Results.Json(result, statusCode: statusCode);
    }
    catch (Exception ex)
    {
        requestLogger.LogError(ex,
            "[{CorrelationId}] /play failed for {RemoteIp} targeting {TargetUrl}",
            correlationId,
            remoteIp,
            targetUrl);

        scrapeHealth.TryReport(
            scrapeContext,
            targetUrl,
            streamName,
            "v1",
            ok: false,
            error: "play execution failed",
            httpStatus: StatusCodes.Status500InternalServerError,
            durationMs: null);

        return Results.Json(new { error = "play execution failed" },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization(StreamViewerAuthorization.PolicyName);

// POST /mp
// V2/MP match pages cannot use Playwright-launched Chromium (page wipes to about:blank).
// This path starts installed Chrome, attaches Network-only CDP, and returns the playlist URL
// plus iframe Referer/Origin and rewritten segment URLs for the client.
app.MapPost("/mp", async (HostMpRequest request, HttpContext httpContext, ScrapeHealthReporter scrapeHealth, ScrapeHealthApiBaseAllowlist scrapeHealthAllowlist, ILogger<Program> requestLogger, CancellationToken ct) =>
{
    var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var correlationId = CorrelationId(httpContext.Request);
    var pageUrl = request.PageUrl?.Trim() ?? "";
    var scrapeContext = ScrapeHealthContextReader.TryRead(httpContext.Request, scrapeHealthAllowlist);
    var streamName = string.IsNullOrWhiteSpace(request.Stream) ? null : request.Stream.Trim();

    if (!mpScrapeGate.Enabled)
    {
        requestLogger.LogInformation("[{CorrelationId}] /mp rejected from {RemoteIp}: V2/MP scrape disabled ({Env}=0)", correlationId, remoteIp, MpScrapeGate.EnvironmentVariable);
        return Results.Json(new { error = "V2/MP scrape disabled on this local service" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrEmpty(pageUrl))
    {
        requestLogger.LogWarning("[{CorrelationId}] /mp request rejected from {RemoteIp}: pageUrl is required", correlationId, remoteIp);
        return Results.BadRequest(new { error = "pageUrl is required" });
    }

    if (!PopProxyHeader.TryRead(httpContext.Connection.RemoteIpAddress, httpContext.Request.Headers[PopProxyHeader.Name], out var popProxy, out var popError))
    {
        requestLogger.LogWarning("[{CorrelationId}] /mp request rejected from {RemoteIp}: {Error}", correlationId, remoteIp, popError);
        return Results.BadRequest(new { error = popError });
    }

    if (!PlayDns.TryReadTemplate(httpContext.Request, out var dohTemplate, out var dohError))
    {
        requestLogger.LogWarning("[{CorrelationId}] /mp request rejected from {RemoteIp}: {Error}", correlationId, remoteIp, dohError);
        return Results.BadRequest(new { error = dohError });
    }

    dohTemplate = PlayDns.SuppressWhenPopProxy(dohTemplate, popProxy);
    var connectionId = ClientConnectionId(httpContext.Request);

    var scrape = StrategyPlugins.Find(httpContext.RequestServices, "VardyParty.LocalService.V2.Scrape", "VardyParty.LocalService.V2.Scrape.V2ScrapePlugin");
    if (scrape is null)
    {
        requestLogger.LogError("[{CorrelationId}] /mp rejected from {RemoteIp}: V2 scrape plugin not registered", correlationId, remoteIp);
        return Results.Json(new { error = "v2 scrape plugin not registered" },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    requestLogger.LogInformation(
        "[{CorrelationId}] /mp request from {RemoteIp} targeting {TargetUrl}{StreamSuffix} doh={Doh} pop={Pop} connection={Connection}",
        correlationId,
        remoteIp,
        pageUrl,
        string.IsNullOrWhiteSpace(request.Stream) ? "" : $" (stream={request.Stream})",
        PlayDns.DohFlag(dohTemplate),
        PlayDns.PopFlag(popProxy),
        connectionId ?? "(none)");

    await MpChromeWaitGate.OneResolve.WaitAsync(ct);
    var pluginType = scrape.GetType();
    var previousDoh = StrategyPlugins.GetStatic(pluginType, "DnsOverHttpsTemplate");
    var previousProxy = StrategyPlugins.GetStatic(pluginType, "ProxyServer");
    var previousChrome = StrategyPlugins.GetStatic(pluginType, "RequestChromeOptions");
    var wrote = false;
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    try
    {
        StrategyPlugins.SetStatic(pluginType, "DnsOverHttpsTemplate", dohTemplate);
        StrategyPlugins.SetStatic(pluginType, "ProxyServer", popProxy);
        StrategyPlugins.SetStatic(pluginType, "RequestChromeOptions", StrategyPlugins.PhoneChromeOptions(popProxy));
        if (popProxy is not null)
        {
            requestLogger.LogInformation(
                "[{CorrelationId}] /mp using the paired phone network; waiting {ChipSeconds}s for chips, {PlaylistSeconds}s for a playlist, {SegmentSeconds}s for segments",
                correlationId,
                (int)PhonePathBudget.ChipWait.TotalSeconds,
                (int)PhonePathBudget.PlaylistWait.TotalSeconds,
                (int)PhonePathBudget.SegmentWait.TotalSeconds);
        }

        if (!request.ListOnly && WantsMpStream(httpContext, request))
        {
            httpContext.Response.ContentType = MpNdjsonRelay.ContentType;
            httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()
                ?.DisableBuffering();
            var pluginRequest = StrategyPlugins.CreateMpRequest(pageUrl, request.Stream, request.ListOnly, request.StreamResults);
            await foreach (var ev in StrategyPlugins.StreamV2Async(scrape, pluginRequest, ct))
            {
                if (!wrote)
                {
                    httpContext.Response.StatusCode = StrategyPlugins.EventType(ev) == "error" && StrategyPlugins.EventStatus(ev) is int status
                        ? status
                        : StatusCodes.Status200OK;
                    wrote = true;
                }

                await httpContext.Response.WriteAsync(JsonSerializer.Serialize(ev, jsonOptions) + "\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }

            if (!wrote)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                await httpContext.Response.WriteAsync(
                    JsonSerializer.Serialize(new { type = "error", statusCode = 404, error = "M3U8 not captured from MP page." }, jsonOptions) + "\n",
                    ct);
            }

            requestLogger.LogInformation("[{CorrelationId}] /mp streamed to {RemoteIp}: status {StatusCode}",
                correlationId, remoteIp, httpContext.Response.StatusCode);
            if (!request.ListOnly)
            {
                var streamedOk = httpContext.Response.StatusCode is >= 200 and < 300;
                scrapeHealth.TryReport(
                    scrapeContext,
                    pageUrl,
                    streamName,
                    "v2",
                    ok: streamedOk,
                    error: streamedOk ? null : "mp stream failed",
                    httpStatus: httpContext.Response.StatusCode,
                    durationMs: null);
            }
            return Results.Empty;
        }

        var outcome = await StrategyPlugins.ResolveV2Async(scrape, StrategyPlugins.CreateMpRequest(pageUrl, request.Stream, request.ListOnly, request.StreamResults), ct);
        var outcomeStatus = StrategyPlugins.StatusCodeOf(outcome);
        var outcomeResult = outcome.GetType().GetProperty("Result")!.GetValue(outcome)!;
        requestLogger.LogInformation("[{CorrelationId}] /mp response to {RemoteIp}: status {StatusCode}", correlationId, remoteIp, outcomeStatus);
        if (!request.ListOnly)
        {
            scrapeHealth.TryReport(
                scrapeContext,
                pageUrl,
                streamName ?? StrategyPlugins.Text(outcomeResult, "SelectedStream"),
                "v2",
                ok: !string.IsNullOrWhiteSpace(StrategyPlugins.Text(outcomeResult, "Url")),
                error: StrategyPlugins.Text(outcomeResult, "Error"),
                httpStatus: outcomeStatus,
                durationMs: StrategyPlugins.TimingsTotal(outcomeResult));
        }
        return Results.Json(outcomeResult, statusCode: outcomeStatus);
    }
    catch (Exception ex)
    {
        requestLogger.LogError(ex,
            "[{CorrelationId}] /mp failed for {RemoteIp} targeting {TargetUrl}",
            correlationId,
            remoteIp,
            pageUrl);

        if (!request.ListOnly)
        {
            scrapeHealth.TryReport(
                scrapeContext,
                pageUrl,
                streamName,
                "v2",
                ok: false,
                error: "mp execution failed",
                httpStatus: StatusCodes.Status500InternalServerError,
                durationMs: null);
        }

        return await MpStreamResponse.CompleteFailureAsync(httpContext, wrote, jsonOptions, ct);
    }
    finally
    {
        StrategyPlugins.SetStatic(pluginType, "RequestChromeOptions", previousChrome);
        StrategyPlugins.SetStatic(pluginType, "DnsOverHttpsTemplate", previousDoh);
        StrategyPlugins.SetStatic(pluginType, "ProxyServer", previousProxy);
        MpChromeWaitGate.OneResolve.Release();
    }
}).RequireAuthorization(StreamViewerAuthorization.PolicyName);

app.MapPost("/compute/host/start", async (HostStartBody body, HttpContext httpContext, ComputeHostAgent agent, ILogger<Program> requestLogger) =>
{
    if (agent.IsSharing)
    {
        return Results.Ok(new { sharing = true });
    }

    if (string.IsNullOrWhiteSpace(body.ApiBaseUrl))
    {
        return Results.BadRequest(new { error = "apiBaseUrl is required" });
    }

    var apiBaseUrl = body.ApiBaseUrl.Trim();
    var authorization = httpContext.Request.Headers.Authorization.ToString();
    try
    {
        await agent.StartAsync(apiBaseUrl, authorization, httpContext.RequestAborted, body.CorrelationId);
        httpContext.RequestServices.GetRequiredService<HostShareStore>().Save(
            new HostShareRecord(apiBaseUrl, authorization.Trim(), body.CorrelationId));
        return Results.Ok(new { sharing = true });
    }
    catch (InvalidOperationException ex)
    {
        requestLogger.LogWarning(ex, "Compute host start rejected");
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization(StreamViewerAuthorization.PolicyName);

app.MapGet("/compute/host/status", (ComputeHostAgent agent) =>
    Results.Ok(new { sharing = agent.IsSharing, inviteUsed = agent.InviteUsed })
).RequireAuthorization(StreamViewerAuthorization.PolicyName);

app.MapPost("/compute/host/stop", async (ComputeHostAgent agent, HostShareStore shareStore) =>
{
    shareStore.Clear();
    await agent.StopAsync();
    return Results.Ok(new { sharing = false });
}).RequireAuthorization(StreamViewerAuthorization.PolicyName);

app.MapPost("/dns", async (DnsPreferenceBody body, IServiceProvider services, ILogger<Program> requestLogger) =>
{
    string? template = null;
    if (body.Enabled)
    {
        if (string.IsNullOrWhiteSpace(body.Resolver)
            || !IPAddress.TryParse(body.Resolver.Trim(), out var address))
        {
            return Results.BadRequest(new { error = "resolver IP is required when DoH is enabled" });
        }

        template = PlayDns.TemplateFor(address);
    }

    var browser = StrategyPlugins.BrowserManager(services);
    if (browser is not null)
        await StrategyPlugins.ApplyDnsAsync(browser, template);

    requestLogger.LogInformation("DoH preference from client: doh={Doh}", PlayDns.DohFlag(template));
    return Results.Ok(new { doh = template is not null });
}).RequireAuthorization(StreamViewerAuthorization.PolicyName);

// Graceful browser manager shutdown on SIGTERM / SIGINT (no-op when not registered).
// System Chrome for /mp is a child process; kill those trees if harvest is still in flight.
lifetime.ApplicationStopping.Register(() =>
{
    OpenedChromeShutdown.CloseAll();
    var bm = StrategyPlugins.BrowserManager(app.Services);
    if (bm is not null)
    {
        StrategyPlugins.DisposeBrowserAsync(bm).GetAwaiter().GetResult();
    }
});
AppDomain.CurrentDomain.ProcessExit += (_, _) => OpenedChromeShutdown.CloseAll();

app.Run();

[SupportedOSPlatform("windows")]
static void ConfigureWindowsHost(WebApplicationBuilder builder)
{
    builder.Host.UseWindowsService();
    builder.Logging.AddEventLog(settings =>
    {
        // The callback is a separate method, so [SupportedOSPlatform] on
        // ConfigureWindowsHost does not cover these Windows-only setters.
        if (!OperatingSystem.IsWindows())
            return;

        settings.LogName = "Application";
        settings.SourceName = "VardyParty.LocalService";
    });
    builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Information);
    builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
    builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.None);
    builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft.AspNetCore.Http.Result", LogLevel.None);
}

static string CorrelationId(HttpRequest request)
{
    var value = request.Headers["X-Correlation-Id"].ToString().Trim();
    return value.Length == 0 ? "-" : value;
}

static string? ClientConnectionId(HttpRequest request)
{
    var value = request.Headers["X-Vardy-Connection"].ToString().Trim();
    return value.Length == 0 ? null : value;
}

static void InsertLocalAppSettings(WebApplicationBuilder builder)
{
    // Testing hosts use WebApplicationFactory.UseSetting for Auth0; a developer
    // appsettings.Local.json must not override those values (or env/CLI).
    if (builder.Environment.IsEnvironment("Testing"))
        return;

    var sources = builder.Configuration.Sources;
    var insertAt = 0;
    for (var i = 0; i < sources.Count; i++)
    {
        if (sources[i] is JsonConfigurationSource json
            && json.Path is not null
            && json.Path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            && !json.Path.Equals("appsettings.Local.json", StringComparison.OrdinalIgnoreCase))
        {
            insertAt = i + 1;
        }
    }

    // After appsettings*.json so Development overrides still apply, but before
    // user-secrets, environment variables, and command-line / UseSetting.
    // reloadOnChange is false: Auth0Settings and JwtBearer options bind once at startup.
    sources.Insert(insertAt, new JsonConfigurationSource
    {
        Path = "appsettings.Local.json",
        Optional = true,
        ReloadOnChange = false,
        FileProvider = builder.Environment.ContentRootFileProvider
    });
}

static bool WantsMpStream(HttpContext http, HostMpRequest request)
{
    if (request.StreamResults)
    {
        return true;
    }

    if (http.Request.Headers.TryGetValue(MpNdjsonRelay.RequestHeaderName, out var flagged)
        && flagged.ToString().Trim() == "1")
    {
        return true;
    }

    var accept = http.Request.Headers.Accept.ToString();
    return accept.Contains("ndjson", StringComparison.OrdinalIgnoreCase);
}

static bool IsHttpPortAvailable(int port)
{
    try
    {
        using var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

static async Task ResumeSavedHostShareAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    var store = services.GetRequiredService<HostShareStore>();
    var saved = store.Load();
    if (saved is null)
    {
        return;
    }

    var resumeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HostShareResume");
    resumeLogger.LogInformation("Resuming compute host sharing after restart");
    try
    {
        await services.GetRequiredService<ComputeHostAgent>()
            .StartAsync(saved.ApiBaseUrl, saved.Authorization, cancellationToken, saved.CorrelationId)
            .ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        if (HostShareResume.ShouldForget(ex))
        {
            store.Clear();
            resumeLogger.LogWarning("Saved host sharing was rejected; share again from the Windows client");
            return;
        }

        resumeLogger.LogWarning(ex, "Could not resume host sharing");
    }
}

public partial class Program;

sealed record DnsPreferenceBody(bool Enabled, string? Resolver);

sealed class HostStartBody
{
    public string? ApiBaseUrl { get; set; }
    public string? CorrelationId { get; set; }
}

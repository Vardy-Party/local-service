using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using VardyParty.LocalService.V1.Scrape;
using VardyParty.LocalService.V1.Scrape.Models;

// In-process V1/FB scrape — same Playwright BrowserManager path LocalService uses —
// then Exo-shaped playlist checks (first bytes #EXTM3U + HlsVideoDataProbe) with soak
// headers. Optional second fetch detects one-shot token burn after the probe GET.
//
// Closer than PlayClientProbe (HTTP /play): no ASP.NET, no JWT, shares LS Chromium cache.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

var pages = new List<string>();
string? stream = null;
var skipBurnCheck = false;
var headful = false;
var verbose = false;
string? doh = null;
string? dumpDir = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--stream" && i + 1 < args.Length)
    {
        stream = args[++i].Trim();
        continue;
    }

    if (args[i] is "--skip-burn-check")
    {
        skipBurnCheck = true;
        continue;
    }

    if (args[i] is "--doh" && i + 1 < args.Length)
    {
        doh = args[++i].Trim();
        continue;
    }

    if (args[i] is "--dump-diagnostics" && i + 1 < args.Length)
    {
        dumpDir = args[++i].Trim();
        Directory.CreateDirectory(dumpDir);
        continue;
    }

    if (args[i] is "--verbose")
    {
        verbose = true;
        continue;
    }

    if (args[i] is "--headful")
    {
        headful = true;
        continue;
    }

    if (args[i] is "--file" && i + 1 < args.Length)
    {
        var path = args[++i];
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith("//"))
                continue;
            pages.Add(t);
        }

        continue;
    }

    if (args[i].StartsWith('-'))
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        PrintHelp();
        return 1;
    }

    pages.Add(args[i].Trim());
}

if (pages.Count == 0)
{
    Console.Error.WriteLine("No page URLs.");
    PrintHelp();
    return 1;
}

foreach (var page in pages)
{
    if (!Uri.TryCreate(page, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
    {
        Console.Error.WriteLine($"Not an http(s) URL: {page}");
        return 1;
    }
}

EnsurePlaywrightBrowsersPath();

var options = new PlaywrightOptions { Headless = !headful, IncludeScreenshots = false };
if (doh is not null && (!Uri.TryCreate(doh, UriKind.Absolute, out var dohUri) || dohUri.Scheme != "https"))
{
    Console.Error.WriteLine($"--doh must be an https template, got: {doh}");
    return 1;
}

await using var browsers = new BrowserManager(
    options,
    verbose ? new ConsoleProbeLogger<BrowserManager>() : NullLogger<BrowserManager>.Instance);
// One engine for every page: a repeated URL exercises the player chain cache (0.1.22+).
var engine = new PlayEngine(
    browsers,
    options,
    verbose ? new ConsoleProbeLogger<PlayEngine>() : NullLogger<PlayEngine>.Instance);

// --doh: Exo-shaped GETs resolve via the same DoH template as Chromium (ConnectCallback).
using var dohResolver = doh is null ? null : new DohResolver(doh);
using var probeHandler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.All
};
if (dohResolver is not null)
    probeHandler.ConnectCallback = dohResolver.ConnectAsync;
using var probeHttp = new HttpClient(probeHandler) { Timeout = TimeSpan.FromMinutes(2) };
var probe = new HlsVideoDataProbe(probeHttp);

Console.WriteLine("=== In-process PlayEngine → Exo-shaped probe ===");
Console.WriteLine($"Browsers: {Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")}");
Console.WriteLine($"Pages: {pages.Count}");
if (!string.IsNullOrWhiteSpace(stream))
    Console.WriteLine($"Stream: {stream}");
Console.WriteLine($"DoH: {doh ?? "(system DNS)"} (Chromium + probe HttpClient)");
Console.WriteLine();

var anyPlayable = false;
var anyFail = false;
PlayRequestOptions? requestOptions = string.IsNullOrWhiteSpace(stream) && doh is null
    ? null
    : new PlayRequestOptions
    {
        StreamName = string.IsNullOrWhiteSpace(stream) ? null : stream,
        DnsOverHttpsTemplate = doh
    };

for (var i = 0; i < pages.Count; i++)
{
    var pageUrl = pages[i];
    Console.WriteLine($"──────── [{i + 1}/{pages.Count}] {pageUrl}");
    var sw = Stopwatch.StartNew();
    PlayResult result;
    try
    {
        result = await engine.ExecuteAsync(pageUrl, requestOptions);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PlayEngine threw after {sw.Elapsed:mm\\:ss\\.fff}: {ex.Message}");
        anyFail = true;
        Console.WriteLine();
        continue;
    }

    Console.WriteLine($"PlayEngine status={result.StatusCode} in {sw.Elapsed:mm\\:ss\\.fff}");
    Console.WriteLine($"  chainCache={result.Diagnostics?.ChainCache ?? "-"}");
    PrintFrameChainAndNetworkFailures(result.Diagnostics);
    DumpDiagnostics(dumpDir, i + 1, pageUrl, result);
    if (!string.IsNullOrWhiteSpace(result.Error))
        Console.WriteLine($"Error: {result.Error}");

    if (string.IsNullOrWhiteSpace(result.Url) || result.StatusCode is < 200 or >= 300)
    {
        Console.WriteLine("No m3u8 URL — scrape miss.");
        PrintM3u8Decisions(result.Diagnostics);
        anyFail = true;
        Console.WriteLine();
        continue;
    }

    Console.WriteLine($"M3U8: {result.Url}");
    PrintHeaders(result.RequestHeaders);
    await PrintDnsComparisonAsync(result.Url, dohResolver);

    Console.WriteLine();
    Console.WriteLine("--- Exo gate: first playlist GET with soak (must be #EXTM3U) ---");
    var gate = await FetchPlaylistHeadAsync(probeHttp, result.Url, result.RequestHeaders);
    PrintGate(gate);
    if (!gate.StartsWithExtM3u)
    {
        Console.WriteLine("Result: FAIL — ExoPlayer would throw ParserException (non-#EXTM3U).");
        anyFail = true;
        Console.WriteLine();
        continue;
    }

    Console.WriteLine();
    Console.WriteLine("--- HlsVideoDataProbe WITH soak ---");
    var soaked = await probe.ProbeAsync(result.Url, result.RequestHeaders);
    PrintReport(soaked);

    if (!soaked.HasVideoData)
    {
        Console.WriteLine();
        Console.WriteLine("--- Segment forensics (first media playlist segment) ---");
        await PrintSegmentForensicsAsync(probeHttp, result.Url, result.RequestHeaders, soaked);
    }

    if (!skipBurnCheck)
    {
        Console.WriteLine();
        Console.WriteLine("--- Burn check: second playlist GET (same URL + soak) ---");
        var burn = await FetchPlaylistHeadAsync(probeHttp, result.Url, result.RequestHeaders);
        PrintGate(burn);
        if (gate.StartsWithExtM3u && !burn.StartsWithExtM3u)
        {
            Console.WriteLine(
                "NOTE: first GET was #EXTM3U, second was not — one-shot token; Client must /play again per attach and must not CDN-probe find URLs.");
        }
        else if (gate.StartsWithExtM3u && burn.StartsWithExtM3u)
        {
            Console.WriteLine("Second GET still #EXTM3U — token reusable from this host (at least twice).");
        }
    }

    Console.WriteLine();
    if (soaked.HasVideoData)
    {
        Console.WriteLine("Result: PLAYABLE (in-process scrape + soak headers).");
        anyPlayable = true;
    }
    else
    {
        Console.WriteLine("Result: playlist OK but no video segment bytes — CDN/segment soak issue.");
        anyFail = true;
    }

    Console.WriteLine();
}

if (anyPlayable && !anyFail)
    return 0;
if (anyPlayable)
    return 4; // mixed
return anyFail ? 3 : 2;

static async Task<PlaylistGate> FetchPlaylistHeadAsync(
    HttpClient http,
    string url,
    IReadOnlyDictionary<string, string>? soak)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    ApplySoak(req, soak);
    try
    {
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[512];
        var read = await stream.ReadAsync(buf);
        var text = Encoding.UTF8.GetString(buf, 0, read);
        var trimmed = text.TrimStart();
        return new PlaylistGate(
            (int)resp.StatusCode,
            trimmed.StartsWith("#EXTM3U", StringComparison.Ordinal),
            HeadSnippet(trimmed),
            null);
    }
    catch (Exception ex)
    {
        return new PlaylistGate(0, false, null, ex.Message);
    }
}

static async Task PrintDnsComparisonAsync(string url, DohResolver? resolver)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        return;
    string sys;
    try
    {
        sys = string.Join(",", (await Dns.GetHostAddressesAsync(u.Host)).Select(a => a.ToString()));
    }
    catch (Exception ex)
    {
        sys = $"error: {ex.Message}";
    }

    Console.WriteLine($"DNS {u.Host}: system={sys}");
    if (resolver is null)
        return;
    try
    {
        var ips = await resolver.ResolveAsync(u.Host);
        Console.WriteLine($"DNS {u.Host}: doh={string.Join(",", ips.Select(a => a.ToString()))} via {resolver.Template}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"DNS {u.Host}: doh error: {ex.Message}");
    }
}

static void ApplySoak(HttpRequestMessage request, IReadOnlyDictionary<string, string>? soak)
{
    if (soak is null)
        return;

    foreach (var (key, value) in soak)
    {
        if (string.IsNullOrWhiteSpace(value))
            continue;
        if (key.Equals("host", StringComparison.OrdinalIgnoreCase))
            continue;
        request.Headers.TryAddWithoutValidation(key, value);
    }
}

static void PrintGate(PlaylistGate gate)
{
    if (!string.IsNullOrWhiteSpace(gate.Error))
    {
        Console.WriteLine($"  fetch error: {gate.Error}");
        return;
    }

    Console.WriteLine($"  HTTP {gate.StatusCode} #EXTM3U={gate.StartsWithExtM3u}");
    if (!string.IsNullOrWhiteSpace(gate.Snippet))
        Console.WriteLine($"  head: {gate.Snippet}");
}

static void PrintHeaders(IReadOnlyDictionary<string, string>? headers)
{
    if (headers is null || headers.Count == 0)
    {
        Console.WriteLine("RequestHeaders: (none)");
        return;
    }

    Console.WriteLine("RequestHeaders:");
    foreach (var (k, v) in headers)
    {
        var shown = k.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            ? $"({v.Length} chars)"
            : v;
        Console.WriteLine($"  {k}: {shown}");
    }
}

static void PrintM3u8Decisions(PlayDiagnostics? diagnostics)
{
    var decisions = diagnostics?.M3u8Decisions;
    if (decisions is null || decisions.Count == 0)
    {
        Console.WriteLine("M3u8Decisions: (none)");
        return;
    }

    Console.WriteLine("M3u8Decisions:");
    foreach (var d in decisions.Take(12))
    {
        var flag = d.Accepted ? "accept" : d.Rejected ? "reject" : "other";
        Console.WriteLine($"  [{flag}] {d.Url} — {d.Reason}");
    }
}

static void PrintReport(HlsVideoProbeReport report)
{
    Console.WriteLine($"HasVideoData: {report.HasVideoData}");
    foreach (var p in report.Playlists)
    {
        var kind = p.IsMaster ? "master" : "media";
        Console.WriteLine($"  [{kind}] HTTP {p.StatusCode} {ShortUrl(p.RequestedUrl)}");
        if (!string.IsNullOrWhiteSpace(p.Note))
            Console.WriteLine($"         note: {p.Note}");
        if (p.Segment is not null)
        {
            Console.WriteLine(
                $"         segment HTTP {p.Segment.StatusCode} {p.Segment.Body.Length} bytes {VideoPayload.Describe(p.Segment.Body)}");
            if (!VideoPayload.LooksLikeVideo(p.Segment.Body))
                Console.WriteLine($"         segment head: {DescribeBytes(p.Segment.Body)}");
        }

        if (!string.IsNullOrWhiteSpace(p.SegmentUrl))
            Console.WriteLine($"         segmentUrl: {ShortUrl(p.SegmentUrl)}");
    }
}

static async Task PrintSegmentForensicsAsync(
    HttpClient http,
    string playlistUrl,
    IReadOnlyDictionary<string, string>? soak,
    HlsVideoProbeReport report)
{
    var media = report.Playlists.FirstOrDefault(p => !p.IsMaster && !string.IsNullOrWhiteSpace(p.SegmentUrl));
    var segmentUrl = media?.SegmentUrl;
    if (string.IsNullOrWhiteSpace(segmentUrl))
    {
        // Re-parse playlist ourselves for the first #EXTINF URI.
        var gate = await FetchPlaylistHeadAsync(http, playlistUrl, soak);
        Console.WriteLine($"  (no segmentUrl on report; playlist head was #EXTM3U={gate.StartsWithExtM3u})");
        return;
    }

    using var req = new HttpRequestMessage(HttpMethod.Get, segmentUrl);
    ApplySoak(req, soak);
    try
    {
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var ct = resp.Content.Headers.ContentType?.ToString() ?? "(none)";
        var cl = resp.Content.Headers.ContentLength?.ToString() ?? "?";
        await using var stream = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[256];
        var read = await stream.ReadAsync(buf);
        var body = buf.AsSpan(0, read).ToArray();
        Console.WriteLine($"  GET {ShortUrl(segmentUrl)}");
        Console.WriteLine($"  HTTP {(int)resp.StatusCode} Content-Type={ct} Content-Length={cl} read={read}");
        Console.WriteLine($"  classify={VideoPayload.Describe(body)} head={DescribeBytes(body)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  segment fetch error: {ex.Message}");
    }
}

static string DescribeBytes(ReadOnlySpan<byte> body)
{
    if (body.Length == 0)
        return "(empty)";

    var hexLen = Math.Min(16, body.Length);
    var hex = Convert.ToHexString(body[..hexLen]);
    // Prefer ASCII when it looks like text (HTML/JSON/m3u8 error pages).
    var printable = 0;
    foreach (var b in body)
    {
        if (b is >= 32 and <= 126 or (byte)'\n' or (byte)'\r' or (byte)'\t')
            printable++;
    }

    if (printable * 2 >= body.Length)
    {
        var text = Encoding.UTF8.GetString(body);
        var one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (one.Length > 100)
            one = one[..97] + "...";
        return $"hex={hex} ascii=\"{one}\"";
    }

    return $"hex={hex}";
}

static void EnsurePlaywrightBrowsersPath()
{
    var existing = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
    if (!string.IsNullOrWhiteSpace(existing) && HasChromium(existing))
        return;

    var lsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VardyParty.LocalService",
        "browsers");
    if (HasChromium(lsPath))
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", lsPath);
        return;
    }

    // Fall back to Playwright default; install chromium if missing.
    var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
    if (exit != 0)
        Console.Error.WriteLine($"warning: playwright install chromium exited {exit}");
}

static bool HasChromium(string browsersPath) =>
    Directory.Exists(browsersPath)
    && Directory.EnumerateDirectories(browsersPath)
        .Any(p => Path.GetFileName(p).StartsWith("chromium", StringComparison.OrdinalIgnoreCase));

static string HeadSnippet(string text)
{
    var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return oneLine.Length <= 120 ? oneLine : oneLine[..117] + "...";
}

static string ShortUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url))
        return "(none)";
    return url.Length <= 96 ? url : url[..93] + "...";
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        In-process PlayEngine (same Chromium as LocalService) + Exo-shaped HLS probe.

        Usage:
          dotnet run --project VardyParty.LocalService.PlayEngineProbe -- <page-url> [more-urls...]
          dotnet run --project VardyParty.LocalService.PlayEngineProbe -- --file urls.txt

        Options:
          --stream NAME       Pass stream chip name into PlayEngine
          --file PATH         One page URL per line (# comments ok)
          --skip-burn-check   Skip second playlist GET
          --headful           Show Chromium window
          --dump-diagnostics <dir>  Write each page's PlayResult + diagnostics JSON to <dir>
          --verbose           Print PlayEngine [/play] log lines (chain cache hit/miss)
          --doh TEMPLATE      DoH template (e.g. https://1.1.1.1/dns-query) for Chromium
                              (PlayRequestOptions.DnsOverHttpsTemplate) and the probe HttpClient

        Uses %LOCALAPPDATA%\VardyParty.LocalService\browsers when present.

        Exit: 0 all playable, 2 scrape miss, 3 not playable, 4 mixed.
        """);
}


static void PrintFrameChainAndNetworkFailures(PlayDiagnostics? diagnostics)
{
    if (diagnostics is null)
        return;

    Console.WriteLine($"  frames ({diagnostics.FrameUrls.Count}): {string.Join(" -> ", diagnostics.FrameUrls)}");
    Console.WriteLine($"  requests={diagnostics.RequestSummary.TotalRequests} responses={diagnostics.RequestSummary.TotalResponses} mediaLike={diagnostics.Interception.MediaLikeRequestCount} failed={diagnostics.FailedRequests.Count}");
    foreach (var host in diagnostics.FailedRequestHosts.Take(15))
    {
        Console.WriteLine(
            $"  failedHost {host.Host} failed={host.Failed} pending={host.Pending} media={host.MediaLike} " +
            $"types=[{string.Join(',', host.ResourceTypes)}] errors=[{string.Join(',', host.Errors)}] e.g. {host.SampleUrl}");
    }
}

static void DumpDiagnostics(string? dumpDir, int index, string pageUrl, PlayResult result)
{
    if (dumpDir is null)
        return;

    var slug = new string(pageUrl.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    if (slug.Length > 80)
        slug = slug[..80];
    var path = Path.Combine(dumpDir, $"{index:00}-{slug}.json");
    var json = System.Text.Json.JsonSerializer.Serialize(
        new { page = pageUrl, result.StatusCode, result.Url, result.Error, result.Timings, result.Diagnostics },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    File.WriteAllText(path, json);
    Console.WriteLine($"  diagnostics -> {path}");
}
readonly record struct PlaylistGate(int StatusCode, bool StartsWithExtM3u, string? Snippet, string? Error);

sealed class ConsoleProbeLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {logLevel}: {formatter(state, exception)}");
    }
}

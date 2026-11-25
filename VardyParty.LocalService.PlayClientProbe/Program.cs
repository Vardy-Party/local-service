using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VardyParty.LocalService.V1.Scrape;

// Call LocalService GET /play (V1/FB) or POST /mp (V2), then HlsVideoDataProbe the
// returned playlist with soak headers (+ rewritten segments for V2). Also probe
// bare (no headers) to show why Client CDN checks fail without soak.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

var pageUrl = args[0].Trim();
if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)
    || pageUri.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("First argument must be an http(s) page URL.");
    return 1;
}

string? stream = null;
string? cookieArg = null;
var useMp = false;
var baseUrl = Environment.GetEnvironmentVariable("LOCAL_SERVICE_BASE")?.Trim()
    ?? "http://127.0.0.1:5019";
for (var i = 1; i < args.Length; i++)
{
    if (args[i] is "--base" && i + 1 < args.Length)
    {
        baseUrl = args[++i].Trim().TrimEnd('/');
        continue;
    }

    if (args[i] is "--stream" && i + 1 < args.Length)
    {
        stream = args[++i].Trim();
        continue;
    }

    if (args[i] is "--cookie" && i + 1 < args.Length)
    {
        cookieArg = args[++i];
        continue;
    }

    if (args[i] is "--mp")
    {
        useMp = true;
        continue;
    }

    Console.Error.WriteLine($"Unknown argument: {args[i]}");
    PrintHelp();
    return 1;
}

var token = ResolveAccessToken(
    cookieArg,
    FirstEnv("VARDY_COOKIE", "SESSION_TOKEN", "VARDY_TOKEN", "LOCAL_SERVICE_TOKEN", "ACCESS_TOKEN"));

// Separate clients: Bearer stays on LocalService only — never forward Auth0 JWT to CDN.
using var localServiceHandler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.All
};
using var localServiceHttp = new HttpClient(localServiceHandler) { Timeout = TimeSpan.FromMinutes(3) };
if (!string.IsNullOrWhiteSpace(token))
    localServiceHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

using var probeHandler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.All
};
using var probeHttp = new HttpClient(probeHandler) { Timeout = TimeSpan.FromMinutes(3) };

Console.WriteLine(useMp
    ? "=== LocalService /mp → HlsVideoDataProbe ==="
    : "=== LocalService /play → HlsVideoDataProbe ===");
Console.WriteLine($"LS:  {baseUrl}");
Console.WriteLine($"Page: {pageUrl}");
if (!string.IsNullOrWhiteSpace(stream))
    Console.WriteLine($"Stream: {stream}");
Console.WriteLine($"Auth: {(string.IsNullOrWhiteSpace(token) ? "(none — need loopback bypass or --cookie)" : "Bearer JWT")}");
Console.WriteLine();

var sw = Stopwatch.StartNew();
PlayWireResult wire;
try
{
    wire = useMp
        ? await CallMpAsync(localServiceHttp, baseUrl, pageUrl, stream)
        : await CallPlayAsync(localServiceHttp, baseUrl, pageUrl, stream);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"LocalService call failed after {sw.Elapsed:mm\\:ss\\.fff}: {ex.Message}");
    return 1;
}

Console.WriteLine($"LocalService HTTP {wire.StatusCode} in {sw.Elapsed:mm\\:ss\\.fff}");
if (wire.StatusCode is < 200 or >= 300)
{
    if (!string.IsNullOrWhiteSpace(wire.Error))
        Console.WriteLine($"Error: {wire.Error}");
    else if (!string.IsNullOrWhiteSpace(wire.BodySnippet))
        Console.WriteLine($"Body: {wire.BodySnippet}");
    else
        Console.WriteLine($"HTTP {wire.StatusCode} with no error field in response.");
    return 2;
}

if (!string.IsNullOrWhiteSpace(wire.Error))
{
    Console.WriteLine($"Error: {wire.Error}");
    return 2;
}

if (string.IsNullOrWhiteSpace(wire.Url))
{
    Console.WriteLine("No playlist URL in response.");
    return 2;
}

Console.WriteLine($"M3U8: {wire.Url}");
PrintHeaders(wire.RequestHeaders);
if (wire.RewrittenSegments is { Count: > 0 })
{
    Console.WriteLine($"Rewritten segments: {wire.RewrittenSegments.Count}");
    foreach (var s in wire.RewrittenSegments.Take(3))
        Console.WriteLine($"  {ShortUrl(s)}");
}

Console.WriteLine();
Console.WriteLine("--- Probe WITH soak headers (Client/ExoPlayer path) ---");
var soaked = await new HlsVideoDataProbe(probeHttp).ProbeAsync(
    wire.Url,
    wire.RequestHeaders,
    wire.RewrittenSegments);
PrintReport(soaked);

Console.WriteLine();
Console.WriteLine("--- Probe BARE (no soak headers — typical TV CDN fail) ---");
var bare = await new HlsVideoDataProbe(probeHttp).ProbeAsync(wire.Url);
PrintReport(bare);

Console.WriteLine();
if (soaked.HasVideoData)
{
    Console.WriteLine("Result: PLAYABLE with LocalService soak headers.");
    if (!bare.HasVideoData)
        Console.WriteLine("Bare probe failed — Client must forward RequestHeaders (Referer/Origin/Cookie), not probe naked.");
    return 0;
}

Console.WriteLine("Result: NOT playable even with soak headers.");
Console.WriteLine("Diagnose: playlist/segment HTTP status and Note above — dead CDN, wrong referer, or scrape returned a bad URL.");
return 3;

static async Task<PlayWireResult> CallPlayAsync(HttpClient http, string baseUrl, string pageUrl, string? stream)
{
    var path = $"{baseUrl.TrimEnd('/')}/play/{Uri.EscapeDataString(pageUrl)}";
    if (!string.IsNullOrWhiteSpace(stream))
        path += $"?stream={Uri.EscapeDataString(stream)}";

    using var response = await http.GetAsync(path);
    var json = await response.Content.ReadAsStringAsync();
    return ParseWire((int)response.StatusCode, json);
}

static async Task<PlayWireResult> CallMpAsync(HttpClient http, string baseUrl, string pageUrl, string? stream)
{
    var path = $"{baseUrl.TrimEnd('/')}/mp";
    var payload = JsonSerializer.Serialize(new
    {
        pageUrl,
        stream = string.IsNullOrWhiteSpace(stream) ? null : stream,
        streamResults = false
    });
    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
    using var response = await http.PostAsync(path, content);
    var json = await response.Content.ReadAsStringAsync();
    return ParseWire((int)response.StatusCode, json);
}

static PlayWireResult ParseWire(int status, string json)
{
    var snippet = string.IsNullOrWhiteSpace(json)
        ? null
        : json.Length <= 240 ? json.Trim() : json.Trim()[..237] + "...";

    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("requestHeaders", out var hdrs) && hdrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in hdrs.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    headers[p.Name] = p.Value.GetString() ?? "";
            }
        }

        List<string>? rewritten = null;
        if (root.TryGetProperty("rewrittenSegments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            rewritten = segs.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
        }

        return new PlayWireResult(
            status,
            root.TryGetProperty("url", out var url) ? url.GetString() : null,
            root.TryGetProperty("error", out var err) ? err.GetString() : null,
            headers.Count > 0 ? headers : null,
            rewritten,
            snippet);
    }
    catch (JsonException)
    {
        return new PlayWireResult(status, null, null, null, null, snippet);
    }
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
        var shown = k.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            ? $"({v.Length} chars)"
            : v;
        Console.WriteLine($"  {k}: {shown}");
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
            Console.WriteLine($"         segment HTTP {p.Segment.StatusCode} {p.Segment.Body.Length} bytes");
    }

    foreach (var s in report.RewrittenSegments)
        Console.WriteLine($"  [rewritten] HTTP {s.StatusCode} {s.Body.Length} bytes {VideoPayload.Describe(s.Body)}");
}

static string? ResolveAccessToken(string? cookieArg, string? envValue)
{
    var raw = cookieArg ?? envValue;
    if (string.IsNullOrWhiteSpace(raw))
        return null;

    raw = raw.Trim();
    if (raw.StartsWith("eyJ", StringComparison.Ordinal))
        return raw;

    // Cookie header: session_token=eyJ...; refresh_token=...
    foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var eq = part.IndexOf('=');
        if (eq <= 0)
            continue;
        var name = part[..eq].Trim();
        var val = part[(eq + 1)..].Trim();
        if (name.Equals("session_token", StringComparison.OrdinalIgnoreCase) && val.StartsWith("eyJ", StringComparison.Ordinal))
            return val;
    }

    return raw.StartsWith("eyJ", StringComparison.Ordinal) ? raw : null;
}

static string? FirstEnv(params string[] names)
{
    foreach (var name in names)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(v))
            return v;
    }

    return null;
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
        Resolve via LocalService, then HlsVideoDataProbe the playlist (with soak
        headers and optional V2 rewritten segments). Compares soaked vs bare fetch.

        Usage:
          dotnet run --project VardyParty.LocalService.PlayClientProbe -- <page-url> `
            [--mp] [--stream NAME] [--base http://127.0.0.1:5019] `
            [--cookie "session_token=eyJ...; refresh_token=..."]

        Env:
          VARDY_COOKIE / SESSION_TOKEN   Auth0 access JWT (same as MpClientProbe)
          LOCAL_SERVICE_BASE             default http://127.0.0.1:5019
          VARDYPARTY_LOOPBACK_AUTH_BYPASS=1 on LocalService skips JWT for loopback probes

        Exit: 0 playable with soak, 2 LS error, 3 not playable even with soak.
        """);
}

readonly record struct PlayWireResult(
    int StatusCode,
    string? Url,
    string? Error,
    Dictionary<string, string>? RequestHeaders,
    List<string>? RewrittenSegments,
    string? BodySnippet);

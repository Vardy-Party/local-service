using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// Wire-identical to VardyParty.Streaming LocalLanPlayService POST /mp:
//   streamResults=true, X-Vardy-Mp-Stream: 1, Accept: application/x-ndjson
// then print each NDJSON line with timings, and simulate V2HarvestSession waits
// (first unlabeled chip vs named siblings that need rewritten samples).

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

var pageUrl = args[0].Trim();
if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)
    || pageUri.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("First argument must be an http(s) match page URL.");
    return 1;
}

string? stream = null;
string? cookieArg = null;
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

    Console.Error.WriteLine($"Unknown argument: {args[i]}");
    PrintHelp();
    return 1;
}

// API browser session stores the Auth0 access JWT in session_token.
// LocalService still wants Authorization: Bearer <jwt>.
var token = ResolveAccessToken(
    cookieArg,
    FirstEnv("VARDY_COOKIE", "SESSION_TOKEN", "VARDY_TOKEN", "LOCAL_SERVICE_TOKEN", "ACCESS_TOKEN"));
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine(
        "Pass the API Cookie header (session_token + refresh_token), e.g.");
    Console.Error.WriteLine(
        "  --cookie \"session_token=eyJ...; refresh_token=...\"");
    Console.Error.WriteLine(
        "Only session_token is sent to LocalService (as Bearer). refresh_token is ignored here.");
    return 1;
}

var mpUrl = $"{baseUrl.TrimEnd('/')}/mp";
var payload = JsonSerializer.Serialize(new
{
    pageUrl,
    stream = string.IsNullOrWhiteSpace(stream) ? null : stream,
    streamResults = true
});

Console.WriteLine("=== MP client wire probe (NDJSON harvest) ===");
Console.WriteLine($"POST {mpUrl}");
Console.WriteLine($"pageUrl: {pageUrl}");
if (!string.IsNullOrWhiteSpace(stream))
{
    Console.WriteLine($"stream:  {stream}");
}

Console.WriteLine($"body:    streamResults=true");
Console.WriteLine($"auth:    Bearer JWT ({token.Length} chars, from API session_token / token env)");
Console.WriteLine($"token:   starts={TokenPreview(token)}  parts={token.Split('.').Length}");
PrintJwtClaims("token claims", token);
Console.WriteLine($"hdrs:    Authorization=Bearer, X-Vardy-Mp-Stream=1, Accept=ndjson");
Console.WriteLine();

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
using var request = new HttpRequestMessage(HttpMethod.Post, mpUrl)
{
    Content = new StringContent(payload, Encoding.UTF8, "application/json")
};
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
request.Headers.TryAddWithoutValidation("X-Vardy-Mp-Stream", "1");
request.Headers.Accept.ParseAdd("application/x-ndjson");
request.Headers.Accept.ParseAdd("application/json");

var sw = Stopwatch.StartNew();
HttpResponseMessage response;
try
{
    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[{Fmt(sw.Elapsed)}] request failed: {ex.Message}");
    return 1;
}

using (response)
{
    var contentType = response.Content.Headers.ContentType?.MediaType ?? "(none)";
    Console.WriteLine(
        $"[{Fmt(sw.Elapsed)}] Δ{Fmt(sw.Elapsed)}  HTTP {(int)response.StatusCode} {response.StatusCode}  Content-Type={contentType}");

    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
    {
        var body = await response.Content.ReadAsStringAsync();
        Console.Error.WriteLine(body);
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "LocalService rejected the Bearer JWT (same OnChallenge for missing OR invalid).");
        Console.Error.WriteLine(
            "Check: token not expired; aud/iss match LocalService Auth0 (AUTH0_DOMAIN / AUTH0_AUDIENCE).");
        Console.Error.WriteLine(
            "AUTH0_DOMAIN must be the custom login host (same as Worker) — not a stale *.auth0.com tenant.");
        Console.Error.WriteLine(
            "session_token from the API host is correct IF LocalService Domain/Audience match that token.");
        PrintJwtClaims("rejected token", token);
        return 1;
    }

    // One-shot JSON = what LocalService.Client still does; Windows/Android client
    // always asks for NDJSON. Surface that clearly when the service falls back.
    if (!contentType.Contains("ndjson", StringComparison.OrdinalIgnoreCase))
    {
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[{Fmt(sw.Elapsed)}] NOT NDJSON — full body after {Fmt(sw.Elapsed)} ({body.Length} chars)");
        Console.WriteLine("(Client LocalLanPlayService would ApplyLegacy on this single JSON.)");
        Console.WriteLine(body);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    await using var streamBody = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(streamBody, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    string[]? chipInventory = null;
    string? selectedFromChips = null;
    TimeSpan? chipsAt = null;
    TimeSpan? firstPlaylistAt = null;
    string? firstPlaylistLabel = null;
    var firstPlaylistRewritten = 0;
    var previousAt = TimeSpan.Zero;
    var lineNo = 0;
    var events = new List<WireEvent>();
    // label -> last wire snapshot (playlist-only then soak-enriched)
    var lastByLabel = new Dictionary<string, WireEvent>(StringComparer.OrdinalIgnoreCase);
    var rewrittenReadyAt = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

    Console.WriteLine();
    Console.WriteLine("--- NDJSON (as LocalLanPlayService reads the body) ---");

    while (true)
    {
        var line = await reader.ReadLineAsync();
        if (line is null)
        {
            break;
        }

        if (line.Length == 0)
        {
            continue;
        }

        lineNo++;
        var at = sw.Elapsed;
        var delta = at - previousAt;
        previousAt = at;

        V2Line? ev;
        try
        {
            ev = JsonSerializer.Deserialize<V2Line>(line, jsonOpts);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[{Fmt(at)}] Δ{Fmt(delta)}  #{lineNo} RAW parse-fail: {ex.Message}");
            Console.WriteLine(line);
            continue;
        }

        if (ev is null || string.IsNullOrWhiteSpace(ev.Type))
        {
            Console.WriteLine($"[{Fmt(at)}] Δ{Fmt(delta)}  #{lineNo} RAW");
            Console.WriteLine(line);
            continue;
        }

        switch (ev.Type.ToLowerInvariant())
        {
            case "chips":
                chipInventory = ev.Streams?.ToArray() ?? [];
                selectedFromChips = ev.SelectedStream;
                chipsAt = at;
                Console.WriteLine(
                    $"[{Fmt(at)}] Δ{Fmt(delta)}  chips ({chipInventory.Length}): {string.Join(", ", chipInventory)}");
                if (!string.IsNullOrWhiteSpace(selectedFromChips))
                {
                    Console.WriteLine($"         selected={selectedFromChips}");
                }

                Console.WriteLine(
                    "         → client SiblingsFrom gets this list on the first chip response");
                break;

            case "chip":
            {
                var label = ev.Label ?? ev.SelectedStream ?? "(no-label)";
                var rewritten = ev.RewrittenSegments?.Count ?? 0;
                var headers = ev.RequestHeaders?.Count ?? 0;
                var wire = new WireEvent(at, label, ev.Url, rewritten, headers);
                events.Add(wire);
                lastByLabel[label] = wire;

                var kind = rewritten > 0 ? "playlist+rewritten" : "playlist-only";
                Console.WriteLine(
                    $"[{Fmt(at)}] Δ{Fmt(delta)}  chip {label}  [{kind}]  url={ShortUrl(ev.Url)}  rewritten={rewritten}  headers={headers}");
                if (rewritten > 0 && ev.RewrittenSegments is not null)
                {
                    foreach (var seg in ev.RewrittenSegments.Take(2))
                    {
                        Console.WriteLine($"         rewritten: {ShortUrl(seg)}");
                    }

                    rewrittenReadyAt.TryAdd(label, at);
                }

                if (firstPlaylistAt is null && !string.IsNullOrWhiteSpace(ev.Url))
                {
                    firstPlaylistAt = at;
                    firstPlaylistLabel = label;
                    firstPlaylistRewritten = rewritten;
                    Console.WriteLine(
                        $"         → client WaitForAsync(null) RETURNS HERE (first playback starts; soak may still be running)");
                }
                else if (rewritten > 0)
                {
                    Console.WriteLine(
                        $"         → client WaitForAsync(\"{label}\") ready for sibling health (rewritten>0)");
                }
                else if (!string.IsNullOrWhiteSpace(ev.Url))
                {
                    Console.WriteLine(
                        $"         → named WaitForAsync(\"{label}\") should WAIT (playlist-only; soak follows)");
                }

                break;
            }

            case "error":
                Console.WriteLine(
                    $"[{Fmt(at)}] Δ{Fmt(delta)}  error status={ev.StatusCode} {ev.Error}");
                if (ev.Streams is { Count: > 0 })
                {
                    Console.WriteLine($"         streams: {string.Join(", ", ev.Streams)}");
                }

                break;

            case "done":
                Console.WriteLine($"[{Fmt(at)}] Δ{Fmt(delta)}  done");
                break;

            default:
                Console.WriteLine($"[{Fmt(at)}] Δ{Fmt(delta)}  {ev.Type}: {line}");
                break;
        }
    }

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine("--- harvest summary (what the client session would observe) ---");
    Console.WriteLine($"total wire time:    {Fmt(sw.Elapsed)}  ({lineNo} NDJSON lines)");
    Console.WriteLine(
        $"chips event:        {(chipsAt is null ? "(never — fan-out cannot run)" : $"{Fmt(chipsAt.Value)}  {chipInventory!.Length} labels")}");
    if (chipInventory is { Length: > 0 })
    {
        Console.WriteLine($"  inventory:        {string.Join(", ", chipInventory)}");
        if (!string.IsNullOrWhiteSpace(selectedFromChips))
        {
            Console.WriteLine($"  selected:         {selectedFromChips}");
        }
    }

    Console.WriteLine(
        $"first WaitFor(null):{(firstPlaylistAt is null ? " (none)" : $" {Fmt(firstPlaylistAt.Value)}  {firstPlaylistLabel}  rewritten={firstPlaylistRewritten}")}");

    if (chipInventory is { Length: > 0 })
    {
        Console.WriteLine();
        Console.WriteLine("per chip (sibling path after first returns):");
        Console.WriteLine(
            $"  {"label",-14} {"playlist",8} {"rewritten*",10} {"soak Δ",8}  url");
        foreach (var label in chipInventory)
        {
            var playlistEvt = events.FirstOrDefault(e =>
                e.Label.Equals(label, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(e.Url));
            var hasPlaylist = playlistEvt.Label is not null;
            var ready = rewrittenReadyAt.TryGetValue(label, out var readyAt);
            var soak = hasPlaylist && ready
                ? Fmt(readyAt - playlistEvt.At)
                : ready
                    ? Fmt(readyAt)
                    : "(none)";
            var last = lastByLabel.TryGetValue(label, out var snap) ? snap : default;
            Console.WriteLine(
                $"  {label,-14} {(hasPlaylist ? Fmt(playlistEvt.At) : "(miss)"),8} {(ready ? Fmt(readyAt) : "(miss)"),10} {soak,8}  {ShortUrl(last.Url)}");
        }

        var missingRewritten = chipInventory.Count(l => !rewrittenReadyAt.ContainsKey(l));
        if (missingRewritten > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"WARNING: {missingRewritten}/{chipInventory.Length} chips never got rewritten>0 — those siblings fail MP health if waited only on playlist-only.");
        }
    }

    return response.IsSuccessStatusCode ? 0 : 1;
}

static string TokenPreview(string token)
{
    if (string.IsNullOrEmpty(token))
    {
        return "(empty)";
    }

    var head = token.Length <= 12 ? token : token[..12];
    return token.StartsWith("eyJ", StringComparison.Ordinal) ? $"{head}…" : $"WARNING not eyJ: {head}…";
}

static void PrintJwtClaims(string title, string token)
{
    var parts = token.Split('.');
    if (parts.Length < 2)
    {
        Console.WriteLine($"{title}: not a JWT (need header.payload.sig)");
        return;
    }

    try
    {
        var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Claim(string name) =>
            root.TryGetProperty(name, out var el)
                ? el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? ""
                    : el.ToString()
                : "(missing)";

        var expRaw = Claim("exp");
        var expNote = expRaw;
        if (long.TryParse(expRaw, out var expSec))
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(expSec);
            var left = exp - DateTimeOffset.UtcNow;
            expNote = $"{exp:u} UTC  ({(left > TimeSpan.Zero ? $"in {left:hh\\:mm\\:ss}" : $"EXPIRED {-left:hh\\:mm\\:ss} ago")})";
        }

        Console.WriteLine($"{title}:");
        Console.WriteLine($"  iss={Claim("iss")}");
        Console.WriteLine($"  aud={Claim("aud")}");
        Console.WriteLine($"  exp={expNote}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{title}: could not decode payload ({ex.Message})");
    }
}

static byte[] Base64UrlDecode(string input)
{
    var s = input.Replace('-', '+').Replace('_', '/');
    switch (s.Length % 4)
    {
        case 2: s += "=="; break;
        case 3: s += "="; break;
    }

    return Convert.FromBase64String(s);
}

static string? FirstEnv(params string[] names)
{
    foreach (var name in names)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

/// <summary>
/// Accepts a bare JWT, <c>Bearer …</c>, <c>session_token=…</c>, or a full
/// Cookie header; returns the access token LocalService expects as Bearer.
/// </summary>
static string? ResolveAccessToken(string? cookieOrTokenArg, string? envValue)
{
    foreach (var raw in new[] { cookieOrTokenArg, envValue })
    {
        var token = ExtractAccessToken(raw);
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }
    }

    return null;
}

static string? ExtractAccessToken(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    var value = raw.Trim();
    if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        value = value["Bearer ".Length..].Trim();
    }

    // Full Cookie header or session_token=eyJ…
    if (value.Contains('=') && !value.StartsWith("eyJ", StringComparison.Ordinal))
    {
        var fromSession = ReadCookie(value, "session_token");
        if (!string.IsNullOrWhiteSpace(fromSession))
        {
            return fromSession;
        }
    }

    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static string? ReadCookie(string cookieHeader, string name)
{
    foreach (var part in cookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        var eq = part.IndexOf('=');
        if (eq <= 0)
        {
            continue;
        }

        var key = part[..eq].Trim();
        if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var val = part[(eq + 1)..].Trim();
        try
        {
            return Uri.UnescapeDataString(val);
        }
        catch (UriFormatException)
        {
            return val;
        }
    }

    return null;
}

static string Fmt(TimeSpan t) =>
    t.TotalMinutes >= 1
        ? $"+{t.TotalMinutes:0}:{t.Seconds:00}.{t.Milliseconds:000}"
        : $"+{t.TotalSeconds:0.000}s";

static string ShortUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url))
    {
        return "(none)";
    }

    return url.Length <= 88 ? url : url[..85] + "...";
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        Stream POST /mp NDJSON the same way LocalLanPlayService does (not the
        one-shot JSON LocalService.Client path).

        Usage:
          # Paste both API cookies from DevTools (Cookie header on the API host)
          dotnet run --project VardyParty.LocalService.V2ClientProbe -- <match-page-url> `
            --cookie "session_token=eyJ...; refresh_token=..." `
            [--base http://127.0.0.1:5019] [--stream "1X EN"]

          # or:
          $env:VARDY_COOKIE = "session_token=eyJ...; refresh_token=..."
          dotnet run --project VardyParty.LocalService.V2ClientProbe -- <match-page-url>

        Env:
          VARDY_COOKIE                   full Cookie header (session_token + refresh_token)
          SESSION_TOKEN | VARDY_TOKEN    session_token value / bare JWT also fine
          LOCAL_SERVICE_BASE             default http://127.0.0.1:5019

        session_token IS the Auth0 access JWT — sent as Authorization: Bearer.
        refresh_token is accepted in the paste but not used by this probe.

        Each line: [+elapsed] Δsince-previous  event…
        Summary shows when WaitForAsync(null) would return vs when each named
        sibling gets rewritten samples for health.
        """);
}

sealed class V2Line
{
    public string Type { get; set; } = "";
    public int? StatusCode { get; set; }
    public List<string>? Streams { get; set; }
    public string? SelectedStream { get; set; }
    public string? Label { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public List<string>? RewrittenSegments { get; set; }
    public string? Error { get; set; }
}

readonly record struct WireEvent(TimeSpan At, string Label, string? Url, int Rewritten, int Headers);

using System.Net;
using Microsoft.Extensions.Configuration;

namespace VardyParty.LocalService.ScrapeHealth;

/// <summary>
/// Optional client headers that enable scrape-health reporting when the user
/// has opted into stream data collection. Missing any required field is a no-op.
/// </summary>
public static class ScrapeHealthHeaders
{
    public const string ApiBaseUrl = "X-VP-Api-Base-Url";
    public const string League = "X-VP-League";
    public const string Home = "X-VP-Home";
    public const string Away = "X-VP-Away";
    public const string ClientOs = "X-VP-Client-Os";
    public const string ClientVersion = "X-VP-Client-Version";
}

public sealed record ScrapeHealthContext(
    string ApiBaseUrl,
    string League,
    string Home,
    string Away,
    string? ClientOs,
    string? ClientVersion,
    string Authorization);

/// <summary>
/// HTTPS API base allowlist for scrape-health POSTs. Hosts come only from
/// configuration / Auth0 audience — never hardcoded production domains.
/// </summary>
public sealed class ScrapeHealthApiBaseAllowlist
{
    private readonly string[] _exactHosts;
    private readonly string[] _suffixes;

    public ScrapeHealthApiBaseAllowlist(IEnumerable<string> entries)
    {
        var exact = new List<string>();
        var suffixes = new List<string>();
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var entry = raw.Trim().TrimStart('.').Trim().ToLowerInvariant();
            if (entry.Length == 0)
                continue;

            // Leading '.' in config → suffix match; bare host → exact (+ its subdomains).
            if (raw.TrimStart().StartsWith('.'))
                suffixes.Add("." + entry);
            else
            {
                exact.Add(entry);
                suffixes.Add("." + entry);
            }
        }

        _exactHosts = exact.Distinct(StringComparer.Ordinal).ToArray();
        _suffixes = suffixes.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Expand a packaging-style CSV (<c>SCRAPE_HEALTH_ALLOWED_API_HOSTS</c>) into
    /// <c>ScrapeHealth:AllowedApiHosts:{n}</c> entries for <see cref="IConfiguration"/>.
    /// Call at process start so <see cref="FromConfiguration"/> stays configuration-only.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> ConfigurationEntriesFromCsv(
        string? csv,
        int startIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Array.Empty<KeyValuePair<string, string?>>();

        var index = Math.Max(0, startIndex);
        var entries = new List<KeyValuePair<string, string?>>();
        foreach (var host in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            entries.Add(new KeyValuePair<string, string?>($"ScrapeHealth:AllowedApiHosts:{index}", host));
            index++;
        }

        return entries;
    }

    /// <summary>
    /// When <c>SCRAPE_HEALTH_ALLOWED_API_HOSTS</c> is set (from-source / systemd), fold
    /// it into configuration so tests and production share one <see cref="IConfiguration"/> surface.
    /// </summary>
    public static void ApplyCsvEnvironmentToConfiguration(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var csv = Environment.GetEnvironmentVariable("SCRAPE_HEALTH_ALLOWED_API_HOSTS");
        var existing = configuration.GetSection("ScrapeHealth:AllowedApiHosts").GetChildren().Count();
        var extras = ConfigurationEntriesFromCsv(csv, existing);
        if (extras.Count == 0)
            return;

        configuration.AddInMemoryCollection(extras);
    }

    public static ScrapeHealthApiBaseAllowlist FromConfiguration(IConfiguration configuration)
    {
        var entries = new List<string>();
        foreach (var host in configuration.GetSection("ScrapeHealth:AllowedApiHosts").Get<string[]>() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(host))
                entries.Add(host);
        }

        // Also accept indexed children when array bind returns null (some providers).
        if (entries.Count == 0)
        {
            foreach (var child in configuration.GetSection("ScrapeHealth:AllowedApiHosts").GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(child.Value))
                    entries.Add(child.Value);
            }
        }

        // Audience host from Auth0:Audience only — Import-VpAuth0Env / run-from-source
        // already mirror AUTH0_AUDIENCE → Auth0__Audience before host build.
        var audience = configuration["Auth0:Audience"];
        if (Uri.TryCreate(audience, UriKind.Absolute, out var audienceUri)
            && audienceUri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(audienceUri.IdnHost)
            && audienceUri.HostNameType == UriHostNameType.Dns)
        {
            entries.Add(audienceUri.IdnHost);
        }

        return new ScrapeHealthApiBaseAllowlist(entries);
    }

    public bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return false;

        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _))
            return false;

        if (_exactHosts.Length == 0 && _suffixes.Length == 0)
            return false;

        var normalized = host.ToLowerInvariant();
        if (_exactHosts.Any(h => normalized.Equals(h, StringComparison.Ordinal)))
            return true;

        return _suffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal));
    }
}

public static class ScrapeHealthContextReader
{
    public static ScrapeHealthContext? TryRead(HttpRequest request, ScrapeHealthApiBaseAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);

        var apiBaseUrl = Header(request, ScrapeHealthHeaders.ApiBaseUrl);
        var league = Header(request, ScrapeHealthHeaders.League);
        var home = Header(request, ScrapeHealthHeaders.Home);
        var away = Header(request, ScrapeHealthHeaders.Away);
        var authorization = request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(apiBaseUrl)
            || string.IsNullOrWhiteSpace(league)
            || string.IsNullOrWhiteSpace(home)
            || string.IsNullOrWhiteSpace(away)
            || string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(apiBaseUrl.TrimEnd('/'), UriKind.Absolute, out var uri)
            || !allowlist.IsAllowed(uri))
        {
            return null;
        }

        return new ScrapeHealthContext(
            uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            league.Trim(),
            home.Trim(),
            away.Trim(),
            NullIfEmpty(Header(request, ScrapeHealthHeaders.ClientOs)),
            NullIfEmpty(Header(request, ScrapeHealthHeaders.ClientVersion)),
            authorization.Trim());
    }

    private static string? Header(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

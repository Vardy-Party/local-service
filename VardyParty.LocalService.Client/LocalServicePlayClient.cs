using System.Text.Json;
using VardyParty.LocalService.Client.Discovery;
using VardyParty.LocalService.Client.Models;

namespace VardyParty.LocalService.Client;

public sealed class LocalServicePlayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly LocalServiceDiscoveryClient _discoveryClient;
    private readonly LocalServiceClientOptions _options;
    private string? _cachedBaseUrl;
    private DateTimeOffset _cachedBaseUrlAt = DateTimeOffset.MinValue;
    private string[] _cachedCapabilities = [];
    private DateTimeOffset _capabilitiesCachedAt = DateTimeOffset.MinValue;

    public LocalServicePlayClient(HttpClient httpClient, LocalServiceClientOptions? options = null)
    {
        _httpClient = httpClient;
        _options = options ?? new LocalServiceClientOptions();
        _discoveryClient = new LocalServiceDiscoveryClient(_options);
    }

    public string? DiscoveredServiceVersion { get; private set; }

    public async Task<bool> SupportsPlayStreamQueryAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        return DiscoveryCapabilities.SupportsPlayStreamQuery(_cachedCapabilities);
    }

    public async Task<string?> GetDiscoveredServiceVersionAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(DiscoveredServiceVersion))
            return DiscoveredServiceVersion;

        _capabilitiesCachedAt = DateTimeOffset.MinValue;
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        return DiscoveredServiceVersion;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = await ResolveBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        if (await CheckHealthAsync(baseUrl, cancellationToken))
        {
            return true;
        }

        InvalidateDiscoveryCache();
        baseUrl = await ResolveBaseUrlAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(baseUrl) && await CheckHealthAsync(baseUrl, cancellationToken);
    }

    public async Task<PlayResult?> ResolveAsync(PlayRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PageUrl))
        {
            return null;
        }

        var baseUrl = await ResolveBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        await RefreshCapabilitiesIfNeededAsync(cancellationToken);

        var effectiveRequest = DiscoveryCapabilities.SupportsPlayStreamQuery(_cachedCapabilities)
            ? request
            : new PlayRequest { PageUrl = request.PageUrl };

        var result = await CallPlayEndpointAsync(baseUrl, effectiveRequest, cancellationToken);
        if (result != null)
        {
            return result;
        }

        InvalidateDiscoveryCache();
        baseUrl = await ResolveBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return await CallPlayEndpointAsync(baseUrl, effectiveRequest, cancellationToken);
    }

    public async Task<bool> SupportsV2ChromeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        return DiscoveryCapabilities.SupportsV2Chrome(_cachedCapabilities);
    }

    private async Task<bool> CheckHealthAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync($"{baseUrl.TrimEnd('/')}/health", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            ApplyHealthPayload(doc.RootElement);
            _capabilitiesCachedAt = DateTimeOffset.UtcNow;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RefreshCapabilitiesIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_capabilitiesCachedAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - _capabilitiesCachedAt < TimeSpan.FromSeconds(_options.DiscoveryCacheSeconds)
            && _cachedCapabilities.Length > 0)
        {
            return;
        }

        var baseUrl = await ResolveBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _cachedCapabilities = [];
            DiscoveredServiceVersion = null;
            _capabilitiesCachedAt = DateTimeOffset.UtcNow;
            return;
        }

        await CheckHealthAsync(baseUrl, cancellationToken);
    }

    private void ApplyHealthPayload(JsonElement root)
    {
        _cachedCapabilities = ParseCapabilities(root);
        var version = ParseServiceVersion(root);
        if (!string.IsNullOrWhiteSpace(version))
            DiscoveredServiceVersion = version;
    }

    private static string[] ParseCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var capabilitiesElement)
            || capabilitiesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return capabilitiesElement.EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string? ParseServiceVersion(JsonElement root)
    {
        if (root.TryGetProperty("serviceVersion", out var serviceVersionEl)
            && serviceVersionEl.ValueKind == JsonValueKind.String)
        {
            var serviceVersion = serviceVersionEl.GetString();
            if (!string.IsNullOrWhiteSpace(serviceVersion))
                return serviceVersion.Trim();
        }

        if (root.TryGetProperty("version", out var versionEl)
            && versionEl.ValueKind == JsonValueKind.String)
        {
            var version = versionEl.GetString();
            if (!string.IsNullOrWhiteSpace(version))
                return version.Trim();
        }

        return null;
    }

    private async Task<PlayResult?> CallPlayEndpointAsync(
        string baseUrl,
        PlayRequest request,
        CancellationToken cancellationToken)
    {
        var playPath = $"{baseUrl.TrimEnd('/')}/play/{Uri.EscapeDataString(request.PageUrl)}";
        if (!string.IsNullOrWhiteSpace(request.StreamName))
        {
            playPath += $"?stream={Uri.EscapeDataString(request.StreamName.Trim())}";
        }

        try
        {
            using var response = await _httpClient.GetAsync(playPath, cancellationToken);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorResult = JsonSerializer.Deserialize<PlayResult>(json, JsonOptions);
                return new PlayResult
                {
                    Error = errorResult?.Error ?? $"Local service returned HTTP {(int)response.StatusCode}",
                    Timings = errorResult?.Timings
                };
            }

            return JsonSerializer.Deserialize<PlayResult>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ResolveBaseUrlAsync(CancellationToken cancellationToken)
    {
        var cacheTtl = TimeSpan.FromSeconds(_options.DiscoveryCacheSeconds);
        if (!string.IsNullOrWhiteSpace(_cachedBaseUrl) && DateTimeOffset.UtcNow - _cachedBaseUrlAt < cacheTtl)
        {
            return _cachedBaseUrl;
        }

        var discovered = await _discoveryClient.DiscoverBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(discovered))
        {
            return null;
        }

        _cachedBaseUrl = discovered.TrimEnd('/');
        _cachedBaseUrlAt = DateTimeOffset.UtcNow;
        return _cachedBaseUrl;
    }

    private void InvalidateDiscoveryCache()
    {
        _cachedBaseUrl = null;
        _cachedBaseUrlAt = DateTimeOffset.MinValue;
        _cachedCapabilities = [];
        DiscoveredServiceVersion = null;
        _capabilitiesCachedAt = DateTimeOffset.MinValue;
    }
}

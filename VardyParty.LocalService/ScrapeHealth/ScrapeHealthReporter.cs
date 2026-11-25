using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace VardyParty.LocalService.ScrapeHealth;

public sealed class ScrapeHealthReporter(
    HttpClient httpClient,
    ILogger<ScrapeHealthReporter> logger,
    IHostApplicationLifetime lifetime)
{
    public static string LocalServiceOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : RuntimeInformation.OSDescription;

    public void TryReport(
        ScrapeHealthContext? context,
        string streamUrl,
        string? streamName,
        string strategy,
        bool ok,
        string? error,
        int? httpStatus,
        long? durationMs)
    {
        if (context is null || string.IsNullOrWhiteSpace(streamUrl))
        {
            return;
        }

        var report = new ScrapeHealthReportBody
        {
            StreamUrl = streamUrl.Trim(),
            StreamName = string.IsNullOrWhiteSpace(streamName) ? null : streamName.Trim(),
            Strategy = strategy,
            Status = ok ? "ok" : "failed",
            Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim(),
            HttpStatus = httpStatus,
            DurationMs = durationMs,
            LocalServiceOs = LocalServiceOs,
            LocalServiceVersion = LocalServiceHostVersion.Current,
            ClientOs = context.ClientOs,
            ClientVersion = context.ClientVersion,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var matchSegment =
            $"{Uri.EscapeDataString(context.Home)}{Uri.EscapeDataString(" v ")}{Uri.EscapeDataString(context.Away)}";
        var url =
            $"{context.ApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(context.League)}/{matchSegment}/scrape-health";

        _ = PostFireAndForgetAsync(url, context.Authorization, report, lifetime.ApplicationStopping);
    }

    private async Task PostFireAndForgetAsync(
        string url,
        string authorization,
        ScrapeHealthReportBody report,
        CancellationToken stoppingToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Content = JsonContent.Create(report);
            using var response = await httpClient.SendAsync(request, stoppingToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Scrape-health POST {Url} returned {StatusCode}",
                    url,
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Scrape-health POST cancelled during host shutdown for {Url}", url);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scrape-health POST failed for {Url}", url);
        }
    }

    private sealed class ScrapeHealthReportBody
    {
        [JsonPropertyName("streamUrl")]
        public string StreamUrl { get; init; } = "";

        [JsonPropertyName("streamName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StreamName { get; init; }

        [JsonPropertyName("strategy")]
        public string Strategy { get; init; } = "v1";

        [JsonPropertyName("status")]
        public string Status { get; init; } = "failed";

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; init; }

        [JsonPropertyName("httpStatus")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? HttpStatus { get; init; }

        [JsonPropertyName("durationMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? DurationMs { get; init; }

        [JsonPropertyName("localServiceOs")]
        public string LocalServiceOs { get; init; } = "";

        [JsonPropertyName("localServiceVersion")]
        public string LocalServiceVersion { get; init; } = "";

        [JsonPropertyName("clientOs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ClientOs { get; init; }

        [JsonPropertyName("clientVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ClientVersion { get; init; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; init; }
    }
}

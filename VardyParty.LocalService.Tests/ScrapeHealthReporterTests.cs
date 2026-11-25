using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.LocalService.ScrapeHealth;

namespace VardyParty.LocalService.Tests;

public sealed class ScrapeHealthReporterTests
{
    [Fact]
    public async Task TryReport_WhenContextMissing_DoesNotPost()
    {
        // Arrange
        var handler = new CaptureHandler();
        var reporter = CreateReporter(handler);

        // Act
        reporter.TryReport(
            context: null,
            streamUrl: "https://streams.example.test/match",
            streamName: "Channel North",
            strategy: "v1",
            ok: true,
            error: null,
            httpStatus: 200,
            durationMs: 12);

        await Task.Delay(50);

        // Assert
        Assert.Empty(handler.Captures);
    }

    [Fact]
    public async Task TryReport_WhenContextPresent_PostsScrapeHealth()
    {
        // Arrange
        var handler = new CaptureHandler();
        var reporter = CreateReporter(handler);
        var context = new ScrapeHealthContext(
            "https://api.example.test",
            "League Alpha",
            "Home United",
            "Away City",
            "windows",
            "2.0.0-test",
            "Bearer token-abc");

        // Act
        reporter.TryReport(
            context,
            streamUrl: "https://streams.example.test/match",
            streamName: "Channel North",
            strategy: "v2",
            ok: true,
            error: null,
            httpStatus: 200,
            durationMs: 42);

        await WaitForAsync(() => handler.Captures.Count > 0, TimeSpan.FromSeconds(2));

        // Assert
        var capture = Assert.Single(handler.Captures);
        Assert.Equal(HttpMethod.Post, capture.Method);
        Assert.Contains("/scrape-health", capture.Path, StringComparison.Ordinal);
        Assert.Contains("/League%20Alpha/", capture.Path, StringComparison.Ordinal);
        Assert.Contains("Home%20United%20v%20Away%20City", capture.Path, StringComparison.Ordinal);
        Assert.Equal("Bearer token-abc", capture.Authorization);
        Assert.Contains("\"strategy\":\"v2\"", capture.Body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"ok\"", capture.Body, StringComparison.Ordinal);
        Assert.Contains("\"clientOs\":\"windows\"", capture.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryReport_WhenPostReturnsNonSuccess_DoesNotThrow_AndLogsWarning()
    {
        // Arrange
        var handler = new CaptureHandler { StatusCode = HttpStatusCode.BadGateway };
        var logger = new CollectingLogger();
        var reporter = new ScrapeHealthReporter(
            new HttpClient(handler),
            logger,
            new TestHostLifetime());
        var context = new ScrapeHealthContext(
            "https://api.example.test",
            "League Alpha",
            "Home United",
            "Away City",
            null,
            null,
            "Bearer token-abc");

        // Act
        var exception = Record.Exception(() => reporter.TryReport(
            context,
            streamUrl: "https://streams.example.test/match",
            streamName: null,
            strategy: "v1",
            ok: false,
            error: "timeout",
            httpStatus: null,
            durationMs: 9));

        await WaitForAsync(() => handler.Captures.Count > 0, TimeSpan.FromSeconds(2));
        await WaitForAsync(() => logger.Warnings.Count > 0, TimeSpan.FromSeconds(2));

        // Assert
        Assert.Null(exception);
        Assert.Single(handler.Captures);
        Assert.Contains(logger.Warnings, w => w.Contains("scrape-health", StringComparison.OrdinalIgnoreCase)
            || w.Contains("502", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryReport_WhenPostReturnsRedirect_DoesNotFollow_AndLogsWarning()
    {
        // Arrange — mirrors Program.cs AllowAutoRedirect = false: a 3xx must not hop.
        var handler = new CaptureHandler
        {
            StatusCode = HttpStatusCode.Redirect,
            RedirectLocation = "https://evil.example/collect"
        };
        var logger = new CollectingLogger();
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var reporter = new ScrapeHealthReporter(httpClient, logger, new TestHostLifetime());
        var context = new ScrapeHealthContext(
            "https://api.example.test",
            "League Alpha",
            "Home United",
            "Away City",
            null,
            null,
            "Bearer token-abc");

        // Act
        reporter.TryReport(
            context,
            streamUrl: "https://streams.example.test/match",
            streamName: "Channel North",
            strategy: "v2",
            ok: true,
            error: null,
            httpStatus: 200,
            durationMs: 12);

        await WaitForAsync(() => handler.Captures.Count > 0, TimeSpan.FromSeconds(2));
        await WaitForAsync(() => logger.Warnings.Count > 0, TimeSpan.FromSeconds(2));

        // Assert
        var capture = Assert.Single(handler.Captures);
        Assert.StartsWith("https://api.example.test/", capture.RequestUri, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", capture.RequestUri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bearer token-abc", capture.Authorization);
        Assert.Contains(logger.Warnings, w => w.Contains("302", StringComparison.Ordinal)
            || w.Contains("scrape-health", StringComparison.OrdinalIgnoreCase));
    }

    private static ScrapeHealthReporter CreateReporter(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            NullLogger<ScrapeHealthReporter>.Instance,
            new TestHostLifetime());

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "timed out waiting for scrape-health POST");
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string RequestUri,
        string? Authorization,
        string Body);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Captures { get; } = [];
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string? RedirectLocation { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? authorization = null;
            if (request.Headers.TryGetValues("Authorization", out var values))
                authorization = values.FirstOrDefault();

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Captures.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.RequestUri?.ToString() ?? string.Empty,
                authorization,
                body));

            var response = new HttpResponseMessage(StatusCode);
            if (!string.IsNullOrWhiteSpace(RedirectLocation))
                response.Headers.Location = new Uri(RedirectLocation);
            return response;
        }
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class CollectingLogger : ILogger<ScrapeHealthReporter>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}

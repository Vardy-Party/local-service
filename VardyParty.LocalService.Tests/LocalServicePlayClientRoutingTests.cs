using System.Net;
using System.Text;
using System.Text.Json;
using VardyParty.LocalService.Abstractions.Models;
using VardyParty.LocalService.Client;
using VardyParty.LocalService.Client.Discovery;
using VardyParty.LocalService.Client.Models;

namespace VardyParty.LocalService.Tests;

public sealed class LocalServicePlayClientRoutingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task ResolveAsync_WhenV2WithMpChrome_PostsMpAndPreservesRewrittenSegments()
    {
        var handler = new StubHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/health")
            {
                var health = JsonSerializer.Serialize(new
                {
                    status = "ok",
                    capabilities = new[] { DiscoveryCapabilities.MpChrome, DiscoveryCapabilities.PlayStreamQuery }
                }, JsonOptions);
                return JsonResponse(health);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/mp")
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("https://example.test/match", body, StringComparison.Ordinal);

                var mp = new MpPlayResult
                {
                    Url = "https://cdn.example/playlist.m3u8",
                    RewrittenSegments = ["https://cdn.example/seg1.ts", "https://cdn.example/seg2.ts"],
                    Streams = ["Chip A", "Chip B"],
                    RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://iframe.example/" }
                };
                return JsonResponse(JsonSerializer.Serialize(mp, JsonOptions));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new LocalServicePlayClient(new HttpClient(handler), new LocalServiceClientOptions
        {
            UseDiscovery = false,
            BaseUrl = "http://127.0.0.1:9"
        });

        var result = await client.ResolveAsync(new PlayRequest
        {
            PageUrl = "https://example.test/match",
            ResolutionStrategy = "v2",
            StreamName = "Chip A"
        });

        Assert.NotNull(result);
        Assert.Equal("https://cdn.example/playlist.m3u8", result!.Url);
        Assert.Equal(["https://cdn.example/seg1.ts", "https://cdn.example/seg2.ts"], result.RewrittenSegments);
        Assert.Equal(["Chip A", "Chip B"], result.Streams);
        Assert.Equal("https://iframe.example/", result.RequestHeaders!["Referer"]);
        Assert.Contains(handler.Paths, p => p == "/mp");
        Assert.DoesNotContain(handler.Paths, p => p.StartsWith("/play", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_WhenV1_UsesPlayEndpoint()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
            {
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    status = "ok",
                    capabilities = new[] { DiscoveryCapabilities.PlayStreamQuery, DiscoveryCapabilities.Mp }
                }, JsonOptions)));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.StartsWith("/play/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    url = "https://cdn.example/v1.m3u8"
                }, JsonOptions)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var client = new LocalServicePlayClient(new HttpClient(handler), new LocalServiceClientOptions
        {
            UseDiscovery = false,
            BaseUrl = "http://127.0.0.1:9"
        });

        var result = await client.ResolveAsync(new PlayRequest
        {
            PageUrl = "https://example.test/fb",
            ResolutionStrategy = "v1",
            StreamName = "s1"
        });

        Assert.NotNull(result);
        Assert.Equal("https://cdn.example/v1.m3u8", result!.Url);
        Assert.Null(result.RewrittenSegments);
        Assert.Contains(handler.Paths, p => p.StartsWith("/play/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, p => p == "/mp");
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public List<string> Paths { get; } = [];

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return await _handler(request, cancellationToken);
        }
    }
}

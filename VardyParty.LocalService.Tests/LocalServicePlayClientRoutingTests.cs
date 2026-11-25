using System.Net;
using System.Text;
using System.Text.Json;
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
        Assert.DoesNotContain(handler.Paths, p => p == "/v2");
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

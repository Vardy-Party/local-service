using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace VardyParty.LocalService.Tests;

public sealed class LocalServiceEndpointTests : IClassFixture<LocalServiceWebApplicationFactory>
{
    private readonly HttpClient _client;

    public LocalServiceEndpointTests(LocalServiceWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOkWithCapabilities()
    {
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("capabilities").ValueKind);
        var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.Contains("play.stream", caps);
        Assert.Contains("v2", caps);
        Assert.Contains("v2.chrome", caps);
        Assert.Contains("scrape.health", caps);
        Assert.DoesNotContain("madplay", caps);
    }

    [Fact]
    public async Task Mp_WhenPageUrlMissing_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/v2", new { pageUrl = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pageUrl is required", doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task Mp_WhenPluginMissing_ReturnsSanitizedServerError()
    {
        using var response = await _client.PostAsJsonAsync("/v2", new
        {
            pageUrl = "https://example.test/match"
        });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("v2 plugin not registered", doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task Play_WhenPluginMissing_ReturnsSanitizedServerError()
    {
        using var response = await _client.GetAsync("/play/" + Uri.EscapeDataString("https://example.test/fb"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("play plugin not registered", doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task Play_WhenDohFlagIsNotZeroOrOne_ReturnsBadRequest()
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/play/" + Uri.EscapeDataString("https://example.test/fb"),
            null,
            ("X-Vardy-Doh", "2"));
        await AssertClientError(response, "X-Vardy-Doh must be 0 or 1");
    }

    [Fact]
    public async Task Mp_WhenDohFlagIsNotZeroOrOne_ReturnsBadRequest()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/v2",
            new { pageUrl = "https://example.test/match" },
            ("X-Vardy-Doh", "2"));
        await AssertClientError(response, "X-Vardy-Doh must be 0 or 1");
    }

    [Fact]
    public async Task Play_WhenDohEnabledAndResolverMissing_ReturnsBadRequest()
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/play/" + Uri.EscapeDataString("https://example.test/fb"),
            null,
            ("X-Vardy-Doh", "1"));
        await AssertClientError(response, "X-Vardy-Doh-Resolver must be an IP address when DoH is enabled");
    }

    [Fact]
    public async Task Play_WhenDohResolverIsNotIp_ReturnsBadRequest()
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/play/" + Uri.EscapeDataString("https://example.test/fb"),
            null,
            ("X-Vardy-Doh", "1"),
            ("X-Vardy-Doh-Resolver", "not-an-ip"));
        await AssertClientError(response, "X-Vardy-Doh-Resolver must be an IP address when DoH is enabled");
    }

    [Fact]
    public async Task Mp_WhenDohEnabledAndResolverMissing_ReturnsBadRequest()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/v2",
            new { pageUrl = "https://example.test/match" },
            ("X-Vardy-Doh", "1"));
        await AssertClientError(response, "X-Vardy-Doh-Resolver must be an IP address when DoH is enabled");
    }

    [Fact]
    public async Task Mp_WhenDohResolverIsNotIp_ReturnsBadRequest()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/v2",
            new { pageUrl = "https://example.test/match" },
            ("X-Vardy-Doh", "1"),
            ("X-Vardy-Doh-Resolver", "not-an-ip"));
        await AssertClientError(response, "X-Vardy-Doh-Resolver must be an IP address when DoH is enabled");
    }

    [Fact]
    public async Task Play_WhenDohDisabled_ReturnsPluginMissing()
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/play/" + Uri.EscapeDataString("https://example.test/fb"),
            null,
            ("X-Vardy-Doh", "0"));
        await AssertPluginMissing(response, "play plugin not registered");
    }

    [Fact]
    public async Task Play_WhenDohHeaderOmitted_ReturnsPluginMissing()
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/play/" + Uri.EscapeDataString("https://example.test/fb"),
            null);
        await AssertPluginMissing(response, "play plugin not registered");
    }

    [Fact]
    public async Task Mp_WhenDohDisabled_ReturnsPluginMissing()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/v2",
            new { pageUrl = "https://example.test/match" },
            ("X-Vardy-Doh", "0"));
        await AssertPluginMissing(response, "v2 plugin not registered");
    }

    [Fact]
    public async Task Mp_WhenDohHeaderOmitted_ReturnsPluginMissing()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/v2",
            new { pageUrl = "https://example.test/match" });
        await AssertPluginMissing(response, "v2 plugin not registered");
    }

    [Fact]
    public async Task Dns_WhenEnabledWithoutResolver_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/dns", new { enabled = true });
        await AssertClientError(response, "resolver IP is required when DoH is enabled");
    }

    [Fact]
    public async Task Dns_WhenResolverIsNotIp_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/dns", new { enabled = true, resolver = "not-an-ip" });
        await AssertClientError(response, "resolver IP is required when DoH is enabled");
    }

    [Fact]
    public async Task Dns_WhenDisabled_ReturnsDohFalseWithoutStartingChromium()
    {
        using var response = await _client.PostAsJsonAsync("/dns", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("doh").GetBoolean());
    }

    [Fact]
    public async Task Dns_WhenEnabledWithResolver_ReturnsDohTrueWithoutStartingChromium()
    {
        using var response = await _client.PostAsJsonAsync("/dns", new { enabled = true, resolver = "1.1.1.1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("doh").GetBoolean());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        return await _client.SendAsync(request);
    }

    private static async Task AssertClientError(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }

    private static async Task AssertPluginMissing(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }
}

public sealed class LocalServiceWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSolutionRelativeContentRoot("VardyParty.LocalService");
        builder.UseSetting("Testing:BypassAuth", "true");
        // Placeholder Auth0 values so hosts that disable bypass still start.
        builder.UseSetting("Auth0:Domain", "example.auth0.com");
        builder.UseSetting("Auth0:Audience", "https://api.example.test");
    }
}

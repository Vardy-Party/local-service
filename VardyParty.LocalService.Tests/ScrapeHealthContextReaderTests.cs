using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VardyParty.LocalService.ScrapeHealth;

namespace VardyParty.LocalService.Tests;

public sealed class ScrapeHealthContextReaderTests
{
    private static readonly ScrapeHealthApiBaseAllowlist Allowlist =
        new(["example.test", ".example.test"]);

    [Fact]
    public void TryRead_ReturnsNull_WhenHeadersMissing()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer token";

        // Act
        var read = ScrapeHealthContextReader.TryRead(context.Request, Allowlist);

        // Assert
        Assert.Null(read);
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenBearerMissing()
    {
        // Arrange
        var context = RequestWithRequiredHeaders(
            "https://api.example.test",
            authorization: null);

        // Act
        var read = ScrapeHealthContextReader.TryRead(context.Request, Allowlist);

        // Assert
        Assert.Null(read);
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenAuthorizationIsNotBearer()
    {
        // Arrange
        var context = RequestWithRequiredHeaders(
            "https://api.example.test",
            authorization: "Basic abc");

        // Act
        var read = ScrapeHealthContextReader.TryRead(context.Request, Allowlist);

        // Assert
        Assert.Null(read);
    }

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://192.168.0.1")]
    [InlineData("https://192.168.0.1")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://evil.example")]
    [InlineData("https://example.com")]
    [InlineData("not-a-uri")]
    [InlineData("ftp://api.example.test")]
    public void TryRead_ReturnsNull_ForRejectedApiBases(string apiBaseUrl)
    {
        // Arrange
        var context = RequestWithRequiredHeaders(apiBaseUrl);

        // Act
        var read = ScrapeHealthContextReader.TryRead(context.Request, Allowlist);

        // Assert
        Assert.Null(read);
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenAllowlistEmpty()
    {
        // Arrange
        var context = RequestWithRequiredHeaders("https://api.example.test");

        // Act
        var read = ScrapeHealthContextReader.TryRead(context.Request, new ScrapeHealthApiBaseAllowlist([]));

        // Assert
        Assert.Null(read);
    }

    [Theory]
    [InlineData("https://api.example.test/", "https://api.example.test")]
    [InlineData("https://preview.example.test", "https://preview.example.test")]
    [InlineData("https://example.test", "https://example.test")]
    public void TryRead_ReturnsContext_WhenRequiredHeadersPresent(string apiBaseUrl, string expectedAuthority)
    {
        // Arrange
        var context = RequestWithRequiredHeaders(apiBaseUrl);
        context.Request.Headers[ScrapeHealthHeaders.ClientOs] = "android";
        context.Request.Headers[ScrapeHealthHeaders.ClientVersion] = "2.0.1";

        // Act
        var read = ScrapeHealthContextReader.TryRead(context.Request, Allowlist);

        // Assert
        Assert.NotNull(read);
        Assert.Equal(expectedAuthority, read!.ApiBaseUrl);
        Assert.Equal("League Alpha", read.League);
        Assert.Equal("Home United", read.Home);
        Assert.Equal("Away City", read.Away);
        Assert.Equal("android", read.ClientOs);
        Assert.Equal("2.0.1", read.ClientVersion);
        Assert.Equal("Bearer abc.def", read.Authorization);
    }

    [Fact]
    public void FromConfiguration_UsesAudienceHost_WithoutHardcodedDomains()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Audience"] = "https://api.example.test/",
                ["ScrapeHealth:AllowedApiHosts:0"] = ".example.test",
            })
            .Build();

        // Act
        var allowlist = ScrapeHealthApiBaseAllowlist.FromConfiguration(config);

        // Assert
        Assert.True(allowlist.IsAllowed(new Uri("https://api.example.test")));
        Assert.True(allowlist.IsAllowed(new Uri("https://preview.example.test")));
        Assert.False(allowlist.IsAllowed(new Uri("https://evil.example.com")));
    }

    [Fact]
    public void FromConfiguration_EmptyAllowlist_AndNonUriAudience_RejectsAll()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Audience"] = "urn:example:api",
            })
            .Build();

        // Act
        var allowlist = ScrapeHealthApiBaseAllowlist.FromConfiguration(config);

        // Assert
        Assert.False(allowlist.IsAllowed(new Uri("https://api.example.test")));
        Assert.False(allowlist.IsAllowed(new Uri("https://preview.example.test")));
    }

    [Fact]
    public void FromConfiguration_ExplicitAllowedApiHosts_AllowWithoutAudience()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ScrapeHealth:AllowedApiHosts:0"] = "preview.example.test",
                ["ScrapeHealth:AllowedApiHosts:1"] = ".cdn.example.test",
            })
            .Build();

        // Act
        var allowlist = ScrapeHealthApiBaseAllowlist.FromConfiguration(config);

        // Assert
        Assert.True(allowlist.IsAllowed(new Uri("https://preview.example.test")));
        Assert.True(allowlist.IsAllowed(new Uri("https://edge.cdn.example.test")));
        Assert.False(allowlist.IsAllowed(new Uri("https://api.example.test")));
    }

    [Fact]
    public void ConfigurationEntriesFromCsv_ExpandsHosts_IntoIndexedKeys()
    {
        // Arrange
        const string csv = " preview.example.test , .cdn.example.test , ";

        // Act
        var entries = ScrapeHealthApiBaseAllowlist.ConfigurationEntriesFromCsv(csv, startIndex: 2);

        // Assert
        Assert.Equal(2, entries.Count);
        Assert.Equal("ScrapeHealth:AllowedApiHosts:2", entries[0].Key);
        Assert.Equal("preview.example.test", entries[0].Value);
        Assert.Equal("ScrapeHealth:AllowedApiHosts:3", entries[1].Key);
        Assert.Equal(".cdn.example.test", entries[1].Value);
    }

    [Fact]
    public void FromConfiguration_CsvFoldedIntoConfig_AllowsHosts_WithoutAmbientEnv()
    {
        // Arrange — simulate Program ApplyCsvEnvironmentToConfiguration / Import expand
        var csvEntries = ScrapeHealthApiBaseAllowlist.ConfigurationEntriesFromCsv(
            "preview.example.test,.cdn.example.test");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(csvEntries)
            .Build();

        // Act
        var allowlist = ScrapeHealthApiBaseAllowlist.FromConfiguration(config);

        // Assert
        Assert.True(allowlist.IsAllowed(new Uri("https://preview.example.test")));
        Assert.True(allowlist.IsAllowed(new Uri("https://edge.cdn.example.test")));
        Assert.False(allowlist.IsAllowed(new Uri("https://evil.example")));
    }

    [Fact]
    public void FromConfiguration_HttpsAudienceInConfig_AddsHost_WithoutAuth0EnvFallback()
    {
        // Arrange — Auth0__Audience already mirrored; no AUTH0_AUDIENCE ambient required
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Audience"] = "https://api.example.test/stream",
            })
            .Build();

        // Act
        var allowlist = ScrapeHealthApiBaseAllowlist.FromConfiguration(config);

        // Assert
        Assert.True(allowlist.IsAllowed(new Uri("https://api.example.test")));
        Assert.False(allowlist.IsAllowed(new Uri("https://other.example.test")));
    }

    private static DefaultHttpContext RequestWithRequiredHeaders(
        string apiBaseUrl,
        string? authorization = "Bearer abc.def")
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        context.Request.Headers[ScrapeHealthHeaders.ApiBaseUrl] = apiBaseUrl;
        context.Request.Headers[ScrapeHealthHeaders.League] = "League Alpha";
        context.Request.Headers[ScrapeHealthHeaders.Home] = "Home United";
        context.Request.Headers[ScrapeHealthHeaders.Away] = "Away City";
        return context;
    }
}

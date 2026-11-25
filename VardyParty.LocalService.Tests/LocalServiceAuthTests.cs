using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VardyParty.LocalService.Auth;

namespace VardyParty.LocalService.Tests;

public sealed class StreamViewerAuthorizationTests
{
    private static Auth0Settings Settings(
        string? claimNamespace = null,
        string? roleClaimType = null) => new()
    {
        Domain = "example.auth0.com",
        Audience = "https://api.example",
        ClaimNamespace = claimNamespace ?? "",
        RequiredRoleClaimType = roleClaimType ?? "",
        RequiredRole = "stream-viewer",
        RequiredPermission = "read:streams"
    };

    [Fact]
    public void HasStreamViewerAccess_WhenUnauthenticated_ReturnsFalse()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(StreamViewerAuthorization.HasStreamViewerAccess(user, Settings()));
    }

    [Fact]
    public void HasStreamViewerAccess_WhenRolesClaimPresent_ReturnsTrue()
    {
        var user = Authenticated(("roles", "stream-viewer"));
        Assert.True(StreamViewerAuthorization.HasStreamViewerAccess(user, Settings()));
    }

    [Fact]
    public void HasStreamViewerAccess_WhenPermissionPresent_ReturnsTrue()
    {
        var user = Authenticated(("permissions", "read:streams"));
        Assert.True(StreamViewerAuthorization.HasStreamViewerAccess(user, Settings()));
    }

    [Fact]
    public void HasStreamViewerAccess_WhenNamespacedRolePresent_ReturnsTrue()
    {
        var user = Authenticated(("https://api.example/roles", "stream-viewer"));
        Assert.True(StreamViewerAuthorization.HasStreamViewerAccess(
            user,
            Settings(claimNamespace: "https://api.example")));
    }

    [Fact]
    public void HasStreamViewerAccess_WhenClientRoleClaimTypePresent_ReturnsTrue()
    {
        var user = Authenticated(("https://northgate.test/roles", "stream-viewer"));
        Assert.True(StreamViewerAuthorization.HasStreamViewerAccess(
            user,
            Settings(roleClaimType: "https://northgate.test/roles")));
    }

    [Fact]
    public void HasStreamViewerAccess_WhenJsonArrayRoleClaim_ReturnsTrue()
    {
        var user = Authenticated(("roles", """["admin","stream-viewer"]"""));
        Assert.True(StreamViewerAuthorization.HasStreamViewerAccess(user, Settings()));
    }

    [Fact]
    public void HasStreamViewerAccess_WhenMissingRole_ReturnsFalse()
    {
        var user = Authenticated(("roles", "scoreboard"));
        Assert.False(StreamViewerAuthorization.HasStreamViewerAccess(user, Settings()));
    }

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }
}

[CollectionDefinition(nameof(Auth0EnvironmentVariableCollection), DisableParallelization = true)]
public sealed class Auth0EnvironmentVariableCollection;

[Collection(nameof(Auth0EnvironmentVariableCollection))]
public sealed class ResolveAuth0SettingsTests : IDisposable
{
    private static readonly string[] EnvKeys =
    [
        "AUTH0_DOMAIN",
        "AUTH0_AUDIENCE",
        "AUTH0_CLAIM_NAMESPACE",
        "AUTH0_REQUIREDROLECLAIMTYPE",
        "AUTH0_REQUIRED_ROLE_CLAIM_TYPE",
        "AUTH0_REQUIREDROLE",
        "AUTH0_REQUIRED_ROLE",
        "AUTH0_REQUIRED_PERMISSION",
        "AUTH0_REQUIREDPERMISSION"
    ];

    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

    public ResolveAuth0SettingsTests()
    {
        foreach (var key in EnvKeys)
        {
            _original[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        foreach (var pair in _original)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }

    [Fact]
    public void EmptyConfig_ReadsWorkerDomainAndAudience()
    {
        Environment.SetEnvironmentVariable("AUTH0_DOMAIN", "https://packaging.auth0.com/");
        Environment.SetEnvironmentVariable("AUTH0_AUDIENCE", "https://api.packaging.test");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("https://packaging.auth0.com/", settings.Domain);
        Assert.Equal("packaging.auth0.com", settings.NormalizedDomain);
        Assert.Equal("https://api.packaging.test", settings.Audience);
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void EmptyConfig_PrefersPackagingRoleClaimTypeAlias()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIREDROLECLAIMTYPE", "https://ns.test/roles");
        Environment.SetEnvironmentVariable("AUTH0_REQUIRED_ROLE_CLAIM_TYPE", "https://ignored.test/roles");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("https://ns.test/roles", settings.RequiredRoleClaimType);
    }

    [Fact]
    public void EmptyConfig_FallsBackToUnderscoredRoleClaimType()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIRED_ROLE_CLAIM_TYPE", "https://fallback.test/roles");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("https://fallback.test/roles", settings.RequiredRoleClaimType);
    }

    [Fact]
    public void EmptyConfig_PrefersPackagingRequiredRoleAlias()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIREDROLE", "packaging-role");
        Environment.SetEnvironmentVariable("AUTH0_REQUIRED_ROLE", "ignored-role");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("packaging-role", settings.RequiredRole);
    }

    [Fact]
    public void EmptyConfig_FallsBackToUnderscoredRequiredRole()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIRED_ROLE", "worker-role");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("worker-role", settings.RequiredRole);
    }

    [Fact]
    public void EmptyConfig_PrefersUnderscoredPermissionThenPackagingAlias()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIRED_PERMISSION", "read:packaging");
        Environment.SetEnvironmentVariable("AUTH0_REQUIREDPERMISSION", "read:ignored");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("read:packaging", settings.RequiredPermission);
    }

    [Fact]
    public void EmptyConfig_FallsBackToPackagingPermissionAlias()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIREDPERMISSION", "read:alias");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("read:alias", settings.RequiredPermission);
    }

    [Fact]
    public void EnvPermission_OverridesConfiguredPermission()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIRED_PERMISSION", "read:from-env");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Domain"] = "configured.auth0.com",
                ["Auth0:Audience"] = "https://api.configured",
                ["Auth0:RequiredPermission"] = "read:from-config",
                ["Auth0:RequiredRole"] = "config-role"
            })
            .Build();

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(config);

        Assert.Equal("read:from-env", settings.RequiredPermission);
        Assert.Equal("config-role", settings.RequiredRole);
        Assert.Equal("configured.auth0.com", settings.Domain);
    }

    [Fact]
    public void EnvRole_OverridesConfiguredRole()
    {
        Environment.SetEnvironmentVariable("AUTH0_REQUIREDROLE", "env-role");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Domain"] = "configured.auth0.com",
                ["Auth0:Audience"] = "https://api.configured",
                ["Auth0:RequiredRole"] = "config-role"
            })
            .Build();

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(config);

        Assert.Equal("env-role", settings.RequiredRole);
    }

    [Fact]
    public void EmptyConfig_ReadsClaimNamespace()
    {
        Environment.SetEnvironmentVariable("AUTH0_CLAIM_NAMESPACE", "https://claims.example/");

        var settings = LocalServiceAuthExtensions.ResolveAuth0Settings(EmptyConfig());

        Assert.Equal("https://claims.example/", settings.ClaimNamespace);
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();
}

public sealed class Auth0SettingsNormalizationTests
{
    [Theory]
    [InlineData("tenant.auth0.com", "tenant.auth0.com")]
    [InlineData("https://tenant.auth0.com/", "tenant.auth0.com")]
    [InlineData("https://Tenant.Auth0.Com/", "tenant.auth0.com")]
    public void NormalizedDomain_StripsHttpsSlashAndLowercases(string domain, string expected)
    {
        var settings = new Auth0Settings { Domain = domain, Audience = "https://api.example" };
        Assert.Equal(expected, settings.NormalizedDomain);
        Assert.Equal($"https://{expected}/", settings.Authority);
        Assert.True(settings.IsConfigured);
    }

    [Theory]
    [InlineData("http://tenant.auth0.com")]
    [InlineData("https://tenant.auth0.com/path")]
    [InlineData("tenant.auth0.com:443")]
    [InlineData("user@tenant.auth0.com")]
    [InlineData("*")]
    public void NormalizedDomain_RejectsInvalidHosts(string domain)
    {
        var settings = new Auth0Settings { Domain = domain, Audience = "https://api.example" };
        Assert.Equal("", settings.NormalizedDomain);
        Assert.Equal("", settings.Authority);
        Assert.False(settings.IsConfigured);
    }

    [Theory]
    [InlineData("apps.example.test", "apps.example.test")]
    [InlineData("https://Apps.Example.Test/", "apps.example.test")]
    [InlineData("http://apps.example.test", null)]
    [InlineData("https://apps.example.test/path", null)]
    [InlineData("apps.example.test:443", null)]
    public void TryNormalizeIssuerHost_MatchesWorkerParity(string entry, string? expected)
    {
        Assert.Equal(expected, Auth0Settings.TryNormalizeIssuerHost(entry));
    }
}

public sealed class LocalServiceAuthEndpointTests : IClassFixture<LocalServiceAuthRequiredWebApplicationFactory>
{
    private readonly HttpClient _client;

    public LocalServiceAuthEndpointTests(LocalServiceAuthRequiredWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_RemainsAnonymous()
    {
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Mp_WithoutBearer_ReturnsUnauthorized()
    {
        using var response = await _client.PostAsJsonAsync("/v2", new { pageUrl = "https://example.test/match" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Missing or invalid authorization header", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Play_WithoutBearer_ReturnsUnauthorized()
    {
        using var response = await _client.GetAsync("/play/" + Uri.EscapeDataString("https://example.test/fb"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// Host factory that keeps Auth0 JWT enforcement on (no Testing auth bypass).
/// </summary>
public sealed class LocalServiceAuthRequiredWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSolutionRelativeContentRoot("VardyParty.LocalService");
        builder.UseSetting("Testing:BypassAuth", "false");
        builder.UseSetting("Auth0:Domain", "example.auth0.com");
        builder.UseSetting("Auth0:Audience", "https://api.example.test");
        builder.UseSetting("Auth0:RequiredRole", "stream-viewer");
        builder.UseSetting("Auth0:RequiredPermission", "read:streams");
    }
}

[CollectionDefinition(nameof(LoopbackAuthBypassEnvironmentCollection), DisableParallelization = true)]
public sealed class LoopbackAuthBypassEnvironmentCollection;

/// <summary>
/// Covers <c>VARDYPARTY_LOOPBACK_AUTH_BYPASS</c>: loopback succeeds without Bearer;
/// non-loopback and env-unset still 401. Serialized so the process env cannot race.
/// </summary>
[Collection(nameof(LoopbackAuthBypassEnvironmentCollection))]
public sealed class LoopbackAuthBypassEndpointTests : IDisposable
{
    private const string EnvKey = LocalServiceAuthExtensions.LoopbackAuthBypassEnvironmentVariable;
    private readonly string? _original;

    public LoopbackAuthBypassEndpointTests()
    {
        _original = Environment.GetEnvironmentVariable(EnvKey);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvKey, _original);
    }

    [Fact]
    public async Task BypassEnabled_LoopbackRemote_AllowsProtectedRouteWithoutBearer()
    {
        Environment.SetEnvironmentVariable(EnvKey, "1");
        await using var factory = new LoopbackAuthBypassWebApplicationFactory(defaultRemoteIp: IPAddress.Loopback);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/play/" + Uri.EscapeDataString("https://example.test/fb"));

        // Auth succeeded; scrape may 4xx/5xx without plugins — must not be 401.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BypassEnabled_NonLoopbackRemote_ReturnsUnauthorized()
    {
        Environment.SetEnvironmentVariable(EnvKey, "1");
        await using var factory = new LoopbackAuthBypassWebApplicationFactory(
            defaultRemoteIp: IPAddress.Parse("192.168.50.10"));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v2", new { pageUrl = "https://example.test/match" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BypassUnset_ProtectedRoute_ReturnsUnauthorized()
    {
        Environment.SetEnvironmentVariable(EnvKey, null);
        await using var factory = new LoopbackAuthBypassWebApplicationFactory(defaultRemoteIp: IPAddress.Loopback);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/play/" + Uri.EscapeDataString("https://example.test/fb"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// Testing host with JWT on, optional loopback bypass via env, and injectable remote IP.
/// </summary>
public sealed class LoopbackAuthBypassWebApplicationFactory(IPAddress defaultRemoteIp)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSolutionRelativeContentRoot("VardyParty.LocalService");
        builder.UseSetting("Testing:BypassAuth", "false");
        builder.UseSetting("Auth0:Domain", "example.auth0.com");
        builder.UseSetting("Auth0:Audience", "https://api.example.test");
        builder.UseSetting("Auth0:RequiredRole", "stream-viewer");
        builder.UseSetting("Auth0:RequiredPermission", "read:streams");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new SetRemoteIpStartupFilter(defaultRemoteIp));
        });
    }
}

file sealed class SetRemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIp;
                await nextMiddleware();
            });
            next(app);
        };
}

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace VardyParty.LocalService.Auth;

public static class LocalServiceAuthExtensions
{
    public const string LoopbackAuthBypassEnvironmentVariable = "VARDYPARTY_LOOPBACK_AUTH_BYPASS";
    private const string SelectorSchemeName = "LocalServiceAuth";

    public static bool IsLoopbackAuthBypassEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(LoopbackAuthBypassEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase);

    public static Auth0Settings ResolveAuth0Settings(IConfiguration configuration)
    {
        var settings = configuration.GetSection(Auth0Settings.SectionName).Get<Auth0Settings>()
            ?? new Auth0Settings();

        // Worker-style env names (AUTH0_DOMAIN) for installers / systemd units.
        // Packaging secrets use AUTH0_REQUIREDROLECLAIMTYPE (no underscores in ROLE/CLAIM).
        if (string.IsNullOrWhiteSpace(settings.Domain))
            settings.Domain = FirstEnv("AUTH0_DOMAIN") ?? "";
        if (string.IsNullOrWhiteSpace(settings.Audience))
            settings.Audience = FirstEnv("AUTH0_AUDIENCE") ?? "";
        if (string.IsNullOrWhiteSpace(settings.ClaimNamespace))
            settings.ClaimNamespace = FirstEnv("AUTH0_CLAIM_NAMESPACE") ?? "";
        if (string.IsNullOrWhiteSpace(settings.RequiredRoleClaimType))
            settings.RequiredRoleClaimType =
                FirstEnv("AUTH0_REQUIREDROLECLAIMTYPE", "AUTH0_REQUIRED_ROLE_CLAIM_TYPE") ?? "";

        var envRole = FirstEnv("AUTH0_REQUIREDROLE", "AUTH0_REQUIRED_ROLE");
        if (!string.IsNullOrWhiteSpace(envRole))
            settings.RequiredRole = envRole;

        var envPermission = FirstEnv("AUTH0_REQUIRED_PERMISSION", "AUTH0_REQUIREDPERMISSION");
        if (!string.IsNullOrWhiteSpace(envPermission))
            settings.RequiredPermission = envPermission;

        return settings;
    }

    public static IServiceCollection AddLocalServiceAuth(
        this IServiceCollection services,
        IHostEnvironment environment,
        IConfiguration configuration,
        Auth0Settings settings)
    {
        services.AddSingleton(settings);

        // Opt-in only: default Testing hosts must set Testing:BypassAuth=true to skip JWT.
        // VARDYPARTY_LOOPBACK_AUTH_BYPASS=1 is for PlayClientProbe / MpClientProbe on the
        // laptop — scoped to loopback remotes only (never a LAN-wide JWT disable).
        var useTestingBypass = environment.IsEnvironment("Testing")
            && configuration.GetValue("Testing:BypassAuth", false);
        var loopbackProbeBypass = IsLoopbackAuthBypassEnabled();

        if (useTestingBypass)
        {
            services.AddAuthentication(TestingAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestingAuthHandler>(
                    TestingAuthHandler.SchemeName,
                    _ => { });
        }
        else if (loopbackProbeBypass)
        {
            ConfigureLoopbackScopedBypass(services, settings);
        }
        else
        {
            if (!settings.IsConfigured)
            {
                throw new InvalidOperationException(
                    "Auth0 is required for LocalService. Set Auth0:Domain and Auth0:Audience " +
                    "(appsettings, user-secrets, Auth0__Domain/Auth0__Audience, or AUTH0_DOMAIN/AUTH0_AUDIENCE).");
            }

            ConfigureJwtBearer(services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme), settings);
        }

        services.AddAuthorization(options =>
        {
            options.AddPolicy(StreamViewerAuthorization.PolicyName, policy =>
                policy.RequireAssertion(ctx =>
                    StreamViewerAuthorization.HasStreamViewerAccess(ctx.User, settings)));
        });

        return services;
    }

    /// <summary>
    /// Loopback-only probe bypass. Non-loopback peers still need a valid JWT when Auth0
    /// is configured; otherwise they are challenged.
    /// </summary>
    private static void ConfigureLoopbackScopedBypass(IServiceCollection services, Auth0Settings settings)
    {
        var logger = LoggerFactory
            .Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .CreateLogger("VardyParty.LocalService.Auth");
        logger.LogWarning(
            "{Env}=1: JWT is bypassed only for loopback remotes. " +
            "Non-loopback clients still require Auth0 (or receive 401). " +
            "Do not enable on a shared LAN host unless the HTTP bind is loopback-only.",
            LoopbackAuthBypassEnvironmentVariable);

        if (settings.IsConfigured)
        {
            var auth = services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = SelectorSchemeName;
                options.DefaultChallengeScheme = SelectorSchemeName;
            });
            auth.AddPolicyScheme(SelectorSchemeName, SelectorSchemeName, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var ip = context.Connection.RemoteIpAddress;
                    if (ip is not null && IPAddress.IsLoopback(ip))
                        return LoopbackAuthBypassHandler.SchemeName;
                    return JwtBearerDefaults.AuthenticationScheme;
                };
            });
            auth.AddScheme<AuthenticationSchemeOptions, LoopbackAuthBypassHandler>(
                LoopbackAuthBypassHandler.SchemeName,
                _ => { });
            ConfigureJwtBearer(auth, settings);
        }
        else
        {
            services.AddAuthentication(LoopbackAuthBypassHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, LoopbackAuthBypassHandler>(
                    LoopbackAuthBypassHandler.SchemeName,
                    _ => { });
        }
    }

    private static void ConfigureJwtBearer(AuthenticationBuilder auth, Auth0Settings settings)
    {
        auth.AddJwtBearer(options =>
        {
            options.Authority = settings.Authority;
            options.Audience = settings.Audience.Trim();
            options.MapInboundClaims = false;
            // First /mp must not hang for minutes if the tenant metadata URL is wrong.
            options.BackchannelTimeout = TimeSpan.FromSeconds(15);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Authority,
                ValidateAudience = true,
                ValidAudience = settings.Audience.Trim(),
                ValidateLifetime = true,
                NameClaimType = ClaimTypes.NameIdentifier,
                RoleClaimType = "roles"
            };
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var path = context.HttpContext.Request.Path.Value;
                    var logger = context.HttpContext.RequestServices
                        .GetService<ILoggerFactory>()
                        ?.CreateLogger("VardyParty.LocalService.Auth");
                    logger?.LogWarning(context.Exception,
                        "JWT authentication failed for {Method} {Path}",
                        context.HttpContext.Request.Method, path);
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetService<ILoggerFactory>()
                        ?.CreateLogger("VardyParty.LocalService.Auth");
                    logger?.LogWarning(
                        "JWT challenge 401 for {Method} {Path} (error={Error})",
                        context.HttpContext.Request.Method,
                        context.HttpContext.Request.Path.Value,
                        context.Error ?? context.AuthenticateFailure?.Message ?? "none");
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    return context.Response.WriteAsync(
                        """{"error":"Missing or invalid authorization header"}""");
                },
                OnForbidden = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetService<ILoggerFactory>()
                        ?.CreateLogger("VardyParty.LocalService.Auth");
                    logger?.LogWarning(
                        "JWT forbidden 403 for {Method} {Path} — token accepted but {RequiredRole} role/{RequiredPermission} permission missing",
                        context.HttpContext.Request.Method,
                        context.HttpContext.Request.Path.Value,
                        settings.RequiredRole,
                        settings.RequiredPermission);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    return context.Response.WriteAsync(
                        """{"error":"Insufficient permissions. Stream viewer role required."}""");
                }
            };
        });
    }

    private static string? FirstEnv(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace VardyParty.LocalService.Auth;

/// <summary>
/// When <c>VARDYPARTY_LOOPBACK_AUTH_BYPASS=1</c>, authenticates only requests whose
/// <see cref="ConnectionInfo.RemoteIpAddress"/> is loopback. Non-loopback peers
/// are challenged (401) — never a host-wide JWT disable on a LAN bind.
/// </summary>
public sealed class LoopbackAuthBypassHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LoopbackAuthBypass";
    public const string EnvironmentVariableName = LocalServiceAuthExtensions.LoopbackAuthBypassEnvironmentVariable;

    public static bool IsEnabled() => LocalServiceAuthExtensions.IsLoopbackAuthBypassEnabled();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ip = Context.Connection.RemoteIpAddress;
        if (ip is null || !IPAddress.IsLoopback(ip))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "VARDYPARTY_LOOPBACK_AUTH_BYPASS applies only to loopback remotes."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "loopback-probe"),
                new Claim("roles", "stream-viewer")
            ],
            SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        await Response.WriteAsync("""{"error":"Missing or invalid authorization header"}""");
    }
}

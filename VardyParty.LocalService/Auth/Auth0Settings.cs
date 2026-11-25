using System.Text.RegularExpressions;

namespace VardyParty.LocalService.Auth;

/// <summary>
/// Auth0 JWT settings for LocalService. Same tenant/audience as the Worker API and Client.
/// </summary>
public sealed class Auth0Settings
{
    public const string SectionName = "Auth0";

    /// <summary>
    /// Bare-host pattern matching Worker <c>auth-issuers.ts</c>. Requires a dotted host
    /// (no scheme, path, query, userinfo, port, or <c>*</c>).
    /// </summary>
    private static readonly Regex BareHost = new(
        @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Auth0 login / JWKS / issuer host (no scheme). Use the custom domain
    /// (same as Worker <c>AUTH0_DOMAIN</c>) — not the legacy <c>*.auth0.com</c>
    /// tenant when tokens are issued on the custom domain.
    /// </summary>
    public string Domain { get; set; } = "";

    /// <summary>API audience the Client requests when obtaining access tokens.</summary>
    public string Audience { get; set; } = "";

    /// <summary>
    /// Optional custom claim namespace (API <c>AUTH0_CLAIM_NAMESPACE</c>).
    /// When set, roles/permissions are also read from <c>{namespace}/roles</c> and <c>{namespace}/permissions</c>.
    /// </summary>
    public string ClaimNamespace { get; set; } = "";

    /// <summary>
    /// Optional Client-style role claim type (e.g. <c>https://api.example/roles</c>).
    /// When empty, only <c>roles</c> / namespaced roles are checked.
    /// </summary>
    public string RequiredRoleClaimType { get; set; } = "";

    /// <summary>Role required for /play and /mp. Default matches the Worker API.</summary>
    public string RequiredRole { get; set; } = "stream-viewer";

    /// <summary>Permission that also grants access (Worker API parity).</summary>
    public string RequiredPermission { get; set; } = "read:streams";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(NormalizedDomain) && !string.IsNullOrWhiteSpace(Audience?.Trim());

    /// <summary>
    /// Tenant host without scheme or trailing slash (lowercased, Worker-parity validation).
    /// Invalid entries (http, path, port, etc.) yield empty.
    /// </summary>
    public string NormalizedDomain => TryNormalizeIssuerHost(Domain) ?? "";

    public string Authority =>
        string.IsNullOrEmpty(NormalizedDomain) ? "" : $"https://{NormalizedDomain}/";

    public string NormalizedClaimNamespace =>
        string.IsNullOrWhiteSpace(ClaimNamespace)
            ? ""
            : ClaimNamespace.Trim().TrimEnd('/');

    /// <summary>
    /// Normalize an Auth0 issuer host entry like Worker <c>issuerHost</c>:
    /// strip <c>https://</c> only, trim trailing slash, lowercase, bare-host validate.
    /// Returns null for http, path, query, userinfo, port, <c>*</c>, or empty.
    /// </summary>
    internal static string? TryNormalizeIssuerHost(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;

        var value = entry.Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = value["https://".Length..];
        if (value.EndsWith('/'))
            value = value[..^1];
        value = value.ToLowerInvariant();

        if (value.Length is 0 or > 253 || !BareHost.IsMatch(value))
            return null;

        return value;
    }
}

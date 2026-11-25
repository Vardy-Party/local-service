using System.Security.Claims;
using System.Text.Json;

namespace VardyParty.LocalService.Auth;

/// <summary>
/// Mirrors Worker API <c>streamViewerAuthMiddleware</c>: allow when the caller has
/// <c>stream-viewer</c> (or configured role) or <c>read:streams</c> (or configured permission).
/// </summary>
public static class StreamViewerAuthorization
{
    public const string PolicyName = "StreamViewer";

    public static bool HasStreamViewerAccess(ClaimsPrincipal user, Auth0Settings settings)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;

        var requiredRole = string.IsNullOrWhiteSpace(settings.RequiredRole)
            ? "stream-viewer"
            : settings.RequiredRole.Trim();
        var requiredPermission = string.IsNullOrWhiteSpace(settings.RequiredPermission)
            ? "read:streams"
            : settings.RequiredPermission.Trim();

        if (HasValue(user, "roles", requiredRole) ||
            HasValue(user, "permissions", requiredPermission))
        {
            return true;
        }

        var ns = settings.NormalizedClaimNamespace;
        if (!string.IsNullOrEmpty(ns) &&
            (HasValue(user, $"{ns}/roles", requiredRole) ||
             HasValue(user, $"{ns}/permissions", requiredPermission)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(settings.RequiredRoleClaimType) &&
            HasValue(user, settings.RequiredRoleClaimType.Trim(), requiredRole))
        {
            return true;
        }

        return false;
    }

    private static bool HasValue(ClaimsPrincipal user, string claimType, string expected)
    {
        foreach (var claim in user.FindAll(claimType))
        {
            if (string.IsNullOrWhiteSpace(claim.Value))
                continue;

            var raw = claim.Value.Trim();
            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String &&
                                string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Fall through to space-delimited parsing.
                }
            }

            // Auth0 may emit space-delimited strings or multi-value claims.
            foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(part, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}

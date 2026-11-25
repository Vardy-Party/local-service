using System.Net;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Loopback-only signal that this /mp or /play call must egress through the
/// paired phone. LAN clients cannot set it.
/// </summary>
public static class PopProxyHeader
{
    public const string Name = "X-Vardy-Pop-Proxy";

    public static bool TryRead(IPAddress? remote, string? header, out string? proxy, out string? error)
    {
        proxy = null;
        error = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            error = "X-Vardy-Pop-Proxy is only accepted from loopback";
            return false;
        }

        var value = header.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !IsLoopbackHost(uri.Host))
        {
            error = "X-Vardy-Pop-Proxy must be a loopback http proxy";
            return false;
        }

        proxy = value;
        return true;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}

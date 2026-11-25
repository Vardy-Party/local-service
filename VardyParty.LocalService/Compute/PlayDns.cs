using System.Net;
using System.Net.Sockets;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Client DoH headers plus the pop-proxy rule: an accepted phone CONNECT
/// launch uses system DNS so Chrome does not DoH-bypass the proxy.
/// </summary>
internal static class PlayDns
{
    public static bool TryReadTemplate(HttpRequest request, out string? template, out string? error) =>
        TryReadTemplate(
            request.Headers["X-Vardy-Doh"].ToString(),
            request.Headers["X-Vardy-Doh-Resolver"].ToString(),
            out template,
            out error);

    public static bool TryReadTemplate(string? dohFlag, string? resolver, out string? template, out string? error)
    {
        template = null;
        error = null;
        if (string.IsNullOrWhiteSpace(dohFlag))
        {
            return true;
        }

        var flag = dohFlag.Trim();
        if (flag is "0" or "false")
        {
            return true;
        }

        if (flag is not ("1" or "true"))
        {
            error = "X-Vardy-Doh must be 0 or 1";
            return false;
        }

        if (string.IsNullOrWhiteSpace(resolver) || !IPAddress.TryParse(resolver.Trim(), out var address))
        {
            error = "X-Vardy-Doh-Resolver must be an IP address when DoH is enabled";
            return false;
        }

        template = TemplateFor(address);
        return true;
    }

    public static string? SuppressWhenPopProxy(string? dohTemplate, string? popProxy) =>
        popProxy is not null ? null : dohTemplate;

    public static string? ForPlay(string? dohFlag, string? resolver, string? popProxy, out string? error)
    {
        if (!TryReadTemplate(dohFlag, resolver, out var template, out error))
        {
            return null;
        }

        return SuppressWhenPopProxy(template, popProxy);
    }

    public static string DohFlag(string? dohTemplate) => dohTemplate is null ? "off" : "on";

    public static string PopFlag(string? popProxy) => popProxy is null ? "unset" : "set";

    public static string TemplateFor(IPAddress address)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return $"https://{host}/dns-query";
    }
}

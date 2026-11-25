using System.Text.Json;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Guest header policy for a relayed <c>/mp</c> or <c>/play</c> call.
/// The host tunnel is the only pop-proxy the request may use.
/// </summary>
internal static class ComputeGuestHeaders
{
    public static void Copy(HttpRequestMessage request, JsonElement headers)
    {
        if (headers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var header in headers.EnumerateObject())
        {
            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals(PopProxyHeader.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
        }
    }

    public static void AssignHostPopProxy(HttpRequestMessage request, string proxyUrl)
    {
        request.Headers.Remove(PopProxyHeader.Name);
        request.Headers.TryAddWithoutValidation(PopProxyHeader.Name, proxyUrl);
    }
}

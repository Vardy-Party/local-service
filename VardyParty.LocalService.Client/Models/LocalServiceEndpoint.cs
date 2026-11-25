namespace VardyParty.LocalService.Client.Models;

public sealed class LocalServiceEndpoint
{
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = [];

    public bool SupportsPlayStreamQuery =>
        Discovery.DiscoveryCapabilities.SupportsPlayStreamQuery(Capabilities);
}

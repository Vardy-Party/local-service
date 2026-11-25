using VardyParty.LocalService.Client.Discovery;

namespace VardyParty.LocalService.Client;

public class LocalServiceClientOptions
{
    public bool UseDiscovery { get; set; } = true;
    public string? BaseUrl { get; set; }
    public int DiscoveryTimeoutMs { get; set; } = 1200;
    public int DiscoveryCacheSeconds { get; set; } = 120;
    public string DiscoveryProbeType { get; set; } = DiscoveryProtocol.ProbeType;
    public string DiscoveryResponseType { get; set; } = DiscoveryProtocol.ResponseType;
    public int DiscoveryProtocolVersion { get; set; } = DiscoveryProtocol.Version;
    public string BroadcastAddress { get; set; } = "255.255.255.255";

    public IReadOnlyList<int> GetDiscoveryPorts() => DiscoveryProtocol.DiscoveryPorts;
}

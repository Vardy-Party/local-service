namespace VardyParty.LocalService.Client.Discovery;

public sealed class DiscoveryProbeMessage
{
    public string Type { get; set; } = DiscoveryProtocol.ProbeType;
    public int Version { get; set; } = DiscoveryProtocol.Version;
}

public sealed class DiscoveryResponseMessage
{
    public string Type { get; set; } = DiscoveryProtocol.ResponseType;
    public int Version { get; set; } = DiscoveryProtocol.Version;
    public string? Name { get; set; }
    public int HttpPort { get; set; } = DiscoveryProtocol.DefaultHttpPort;
    public string? InstanceId { get; set; }
    public List<string> Capabilities { get; set; } = [];
}

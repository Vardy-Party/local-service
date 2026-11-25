namespace VardyParty.LocalService.Client.Discovery;

public static class DiscoveryProtocol
{
    public const string ServiceType = "_vardy-party-api._tcp.local";
    public const string ProbeType = "vardy-party-discovery";
    public const string ResponseType = "vardy-party-discovery-response";
    public const int Version = 1;

    /// <summary>Capabilities advertised by current local service builds (optional in discovery/health).</summary>
    public static readonly string[] ServiceCapabilities = DiscoveryCapabilities.All;

    public static readonly int[] HttpPorts = [5019, 5137, 5273, 5417, 5573];
    public const int DefaultHttpPort = 5019;

    public static readonly int[] DiscoveryPorts = [40129, 40143, 40157, 40171, 40189];
}

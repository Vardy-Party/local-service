using Makaretu.Dns;
using VardyParty.LocalService.Client.Discovery;

namespace VardyParty.LocalService.Discovery;

/// <summary>
/// Advertises the service on the LAN via mDNS (Multicast DNS / Bonjour / Avahi).
/// Service type: _vardy-party-api._tcp.local
/// Clients can resolve this without any prior knowledge of the server's IP address.
/// </summary>
public sealed class MdnsAdvertisementService(
    IConfiguration config,
    DiscoveryRuntimeState runtimeState,
    ILogger<MdnsAdvertisementService> logger)
    : BackgroundService
{
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var httpPort = runtimeState.HttpPort;
        var serviceName = config["Discovery:ServiceName"] ?? "VardyParty Local Service";

        try
        {
            _mdns = new MulticastService();
            _sd = new ServiceDiscovery(_mdns);

            var profile = new ServiceProfile(
                instanceName: serviceName,
                serviceName: "_vardy-party-api._tcp",
                port: (ushort)httpPort
            );
            profile.AddProperty("version", "1");
            profile.AddProperty("serviceVersion", LocalServiceHostVersion.Current);
            profile.AddProperty("api", "http");
            profile.AddProperty("path", "/play/:url");
            profile.AddProperty("capabilities", string.Join(",", DiscoveryProtocol.ServiceCapabilities));

            _sd.Advertise(profile);
            _mdns.Start();

            logger.LogInformation(
                "mDNS advertising '{ServiceName}' as _vardy-party-api._tcp on port {Port}",
                serviceName, httpPort);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "mDNS advertising failed to start. UDP discovery remains available if enabled.");
            return;
        }

        // Keep running until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public override void Dispose()
    {
        _mdns?.Stop();
        _sd?.Dispose();
        _mdns?.Dispose();
        base.Dispose();
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VardyParty.LocalService.Client.Discovery;

// LocalServiceHostVersion lives in the parent namespace.
namespace VardyParty.LocalService.Discovery;

/// <summary>
///     Listens for UDP broadcast discovery probes on the configured port and replies
///     with service identity so LAN clients can locate the service without mDNS.
///     Client probe payload:  {"type":"vardy-party-discovery","version":1}
///     Service reply payload:
///     {"type":"vardy-party-discovery-response","version":1,"name":"...","httpPort":5019,"instanceId":"..."}
/// </summary>
public sealed class UdpDiscoveryService(
    IConfiguration config,
    DiscoveryRuntimeState runtimeState,
    IFirewallRuleService firewallRuleService,
    ILogger<UdpDiscoveryService> logger) : BackgroundService
{
    private const int SioUdpConnReset = -1744830452;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var udpPorts = DiscoveryProtocol.DiscoveryPorts
            .Where(port => port > 0)
            .Distinct()
            .ToList();

        var httpPort = runtimeState.HttpPort;
        var serviceName = config["Discovery:ServiceName"] ?? "VardyParty Local Service";
        var instanceId = Environment.MachineName;

        UdpClient? udp = null;
        var activeUdpPort = 0;
        foreach (var udpPort in udpPorts)
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort))
                {
                    EnableBroadcast = true
                };

                if (OperatingSystem.IsWindows())
                    try
                    {
                        udp.Client.IOControl((IOControlCode)SioUdpConnReset, [0, 0, 0, 0], null);
                    }
                    catch (Exception ex) when (ex is SocketException or NotSupportedException)
                    {
                        logger.LogDebug(ex, "Unable to disable UDP connection reset behavior on this socket.");
                    }

                activeUdpPort = udpPort;
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                logger.LogWarning(ex,
                    "UDP discovery port {Port} is already in use. Trying next fallback port.",
                    udpPort);
            }

        if (udp == null)
        {
            logger.LogWarning(
                "UDP discovery disabled because all configured ports are in use: {Ports}. HTTP API remains available.",
                string.Join(",", udpPorts));
            return;
        }

        using (udp)
        {
            try
            {
                await firewallRuleService.EnsureUdpDiscoveryRuleAsync(activeUdpPort, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to ensure firewall rule for active UDP discovery port {Port}",
                    activeUdpPort);
            }

            logger.LogInformation("UDP discovery listening on port {Port}", activeUdpPort);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                    try
                    {
                        var result = await udp.ReceiveAsync(stoppingToken);
                        var text = Encoding.UTF8.GetString(result.Buffer);

                        using var doc = JsonDocument.Parse(text);
                        if (!doc.RootElement.TryGetProperty("type", out var typeEl)
                            || typeEl.GetString() != DiscoveryProtocol.ProbeType)
                            continue;

                        logger.LogDebug("Discovery probe received from {Endpoint} on UDP port {Port}",
                            result.RemoteEndPoint, activeUdpPort);

                        var reply = JsonSerializer.Serialize(new
                        {
                            type = DiscoveryProtocol.ResponseType,
                            version = DiscoveryProtocol.Version,
                            serviceVersion = LocalServiceHostVersion.Current,
                            name = serviceName,
                            httpPort,
                            instanceId,
                            discoveryPort = activeUdpPort,
                            capabilities = DiscoveryProtocol.ServiceCapabilities
                        });

                        var replyBytes = Encoding.UTF8.GetBytes(reply);
                        await udp.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
                        logger.LogDebug("Discovery reply sent to {Endpoint} from UDP port {Port}",
                            result.RemoteEndPoint, activeUdpPort);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        logger.LogDebug(ex, "Ignoring expected UDP connection reset from remote endpoint.");
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed probes
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "UDP discovery error");
                    }
            }
            finally
            {
                try
                {
                    await firewallRuleService.RemoveUdpDiscoveryRuleAsync(activeUdpPort, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to remove firewall rule for UDP discovery port {Port}",
                        activeUdpPort);
                }
            }
        }
    }
}
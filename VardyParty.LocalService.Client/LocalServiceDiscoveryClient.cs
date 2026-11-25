using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VardyParty.LocalService.Client.Discovery;
using VardyParty.LocalService.Client.Models;

namespace VardyParty.LocalService.Client;

public sealed class LocalServiceDiscoveryClient(LocalServiceClientOptions options)
{
    public async Task<string?> DiscoverBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        if (!options.UseDiscovery && !string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return options.BaseUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return options.BaseUrl.TrimEnd('/');
        }

        return await DiscoverViaUdpAsync(cancellationToken);
    }

    private async Task<string?> DiscoverViaUdpAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var probePayload = JsonSerializer.Serialize(new
        {
            type = options.DiscoveryProbeType,
            version = options.DiscoveryProtocolVersion
        });

        var bytes = Encoding.UTF8.GetBytes(probePayload);
        var broadcastAddress = string.IsNullOrWhiteSpace(options.BroadcastAddress)
            ? IPAddress.Broadcast
            : IPAddress.Parse(options.BroadcastAddress);

        foreach (var port in options.GetDiscoveryPorts())
        {
            try
            {
                await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(broadcastAddress, port));
            }
            catch
            {
                /* ignore per-port send failures */
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(options.DiscoveryTimeoutMs));

        while (!timeoutCts.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                var receiveTask = udp.ReceiveAsync();
                var completed = await Task.WhenAny(receiveTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
                if (completed != receiveTask)
                {
                    break;
                }

                received = await receiveTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(received.Buffer);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), options.DiscoveryResponseType, StringComparison.Ordinal))
            {
                continue;
            }

            if (!root.TryGetProperty("httpPort", out var portElement) ||
                !portElement.TryGetInt32(out var httpPort) ||
                httpPort <= 0)
            {
                continue;
            }

            return $"http://{received.RemoteEndPoint.Address}:{httpPort}";
        }

        return null;
    }
}

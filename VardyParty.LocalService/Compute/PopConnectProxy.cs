using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Loopback HTTP proxy. CONNECT (and absolute-form HTTP) on ports 80 and 443
/// is handed to <paramref name="open"/>, which dials through the paired phone.
/// Chrome/Google infra is opened on Windows instead so it does not stall match assets.
/// </summary>
public sealed class PopConnectProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, int, CancellationToken, Task<Stream>> _open;
    private readonly Func<string, int, CancellationToken, Task<Stream>> _openDirect;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    public PopConnectProxy(
        Func<string, int, CancellationToken, Task<Stream>> open,
        Func<string, int, CancellationToken, Task<Stream>>? openDirect = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _openDirect = openDirect ?? OpenDirectAsync;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public string ProxyUrl => $"http://127.0.0.1:{Port}";

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            var socket = client.GetStream();
            HeaderRead header;
            try
            {
                header = await ReadHeaderAsync(socket, _stop.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (!TryParseTarget(header.Text, out var host, out var port, out var connect, out var rewritten))
            {
                await WriteAsciiAsync(socket, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                return;
            }

            if (port is not (80 or 443))
            {
                await WriteAsciiAsync(socket, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                return;
            }

            Stream upstream;
            try
            {
                var open = PopProxyDirect.ShouldLeaveWindows(host) ? _openDirect : _open;
                upstream = await open(host, port, _stop.Token).ConfigureAwait(false);
            }
            catch
            {
                await WriteAsciiAsync(socket, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                return;
            }

            await using (upstream)
            {
                if (connect)
                {
                    await WriteAsciiAsync(socket, "HTTP/1.1 200 Connection Established\r\n\r\n").ConfigureAwait(false);
                }
                else
                {
                    var bytes = Encoding.ASCII.GetBytes(rewritten);
                    await upstream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                }

                if (!header.Remainder.IsEmpty)
                {
                    await upstream.WriteAsync(header.Remainder, _stop.Token).ConfigureAwait(false);
                }

                using var copy = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var clientToUpstream = socket.CopyToAsync(upstream, copy.Token);
                var upstreamToClient = upstream.CopyToAsync(socket, copy.Token);
                await Task.WhenAny(clientToUpstream, upstreamToClient).ConfigureAwait(false);
                copy.Cancel();
                try
                {
                    await Task.WhenAll(clientToUpstream, upstreamToClient).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // the slower copy was cancelled after the first side ended
                }
            }
        }
    }

    internal static bool TryParseTarget(
        string header,
        out string host,
        out int port,
        out bool connect,
        out string rewritten)
    {
        host = "";
        port = 0;
        connect = false;
        rewritten = "";
        var first = header.Split("\r\n", 2)[0];
        var parts = first.Split(' ');
        if (parts.Length < 2)
        {
            return false;
        }

        if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            connect = true;
            var hostPort = parts[1];
            var colon = hostPort.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], out port))
            {
                return false;
            }

            host = hostPort[..colon];
            return host.Length > 0;
        }

        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri))
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port;
        var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var rest = header.Contains("\r\n", StringComparison.Ordinal)
            ? header[(header.IndexOf("\r\n", StringComparison.Ordinal))..]
            : "\r\n";
        rewritten = $"{parts[0]} {path} HTTP/1.1{rest}";
        if (!rewritten.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            rewritten += "\r\n";
        }

        return host.Length > 0;
    }

    private static async Task<Stream> OpenDirectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private readonly struct HeaderRead
    {
        public required string Text { get; init; }
        public required ReadOnlyMemory<byte> Remainder { get; init; }
    }

    private static async Task<HeaderRead> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            used += read;
            var end = IndexOfHeaderTerminator(buffer.AsSpan(0, used));
            if (end < 0)
            {
                continue;
            }

            var headerLen = end + 4;
            var remainder = headerLen < used
                ? buffer.AsSpan(headerLen, used - headerLen).ToArray()
                : [];
            return new HeaderRead
            {
                Text = Encoding.ASCII.GetString(buffer, 0, headerLen),
                Remainder = remainder
            };
        }

        throw new IOException("proxy header was incomplete");
    }

    private static int IndexOfHeaderTerminator(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == (byte)'\r'
                && bytes[i + 1] == (byte)'\n'
                && bytes[i + 2] == (byte)'\r'
                && bytes[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static Task WriteAsciiAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes).AsTask();
    }
}

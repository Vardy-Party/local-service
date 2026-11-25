using System.Net;
using System.Net.Sockets;
using System.Text;
using VardyParty.LocalService.Compute;

namespace VardyParty.LocalService.Tests;

public sealed class PopProxyHeaderTests
{
    [Fact]
    public void MissingHeader_IsNotAProxySession()
    {
        var ok = PopProxyHeader.TryRead(IPAddress.Parse("203.0.113.5"), null, out var proxy, out var error);

        Assert.True(ok);
        Assert.Null(proxy);
        Assert.Null(error);
    }

    [Fact]
    public void RemoteClient_CannotSetTheProxy()
    {
        var ok = PopProxyHeader.TryRead(
            IPAddress.Parse("203.0.113.5"),
            "http://127.0.0.1:9",
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal("X-Vardy-Pop-Proxy is only accepted from loopback", error);
    }

    [Fact]
    public void Loopback_AcceptsALoopbackProxy()
    {
        var ok = PopProxyHeader.TryRead(IPAddress.Loopback, "http://127.0.0.1:9", out var proxy, out var error);

        Assert.True(ok);
        Assert.Equal("http://127.0.0.1:9", proxy);
        Assert.Null(error);
    }

    [Fact]
    public void Loopback_AcceptsLocalhostAndIpv6Loopback()
    {
        Assert.True(PopProxyHeader.TryRead(IPAddress.Loopback, "http://localhost:9", out var localhost, out var localhostError));
        Assert.Equal("http://localhost:9", localhost);
        Assert.Null(localhostError);

        Assert.True(PopProxyHeader.TryRead(IPAddress.IPv6Loopback, "http://[::1]:9", out var ipv6, out var ipv6Error));
        Assert.Equal("http://[::1]:9", ipv6);
        Assert.Null(ipv6Error);
    }

    [Fact]
    public void Loopback_RejectsHttpsAndNonLoopbackHost()
    {
        Assert.False(PopProxyHeader.TryRead(IPAddress.Loopback, "https://127.0.0.1:9", out _, out var httpsError));
        Assert.Equal("X-Vardy-Pop-Proxy must be a loopback http proxy", httpsError);

        Assert.False(PopProxyHeader.TryRead(IPAddress.Loopback, "http://10.0.0.1:9", out _, out var remoteError));
        Assert.Equal("X-Vardy-Pop-Proxy must be a loopback http proxy", remoteError);
    }
}

public sealed class PopConnectProxyTests
{
    [Fact]
    public async Task Connect_RelaysBytesAndRejectsOtherPorts()
    {
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        var leftover = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x01, 0xFF };
        var echoTask = Task.Run(async () =>
        {
            using var client = await echo.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[leftover.Length];
            await stream.ReadExactlyAsync(buffer);
            Assert.Equal(leftover, buffer);
            await stream.WriteAsync(buffer);
        });

        var opened = 0;
        await using var proxy = new PopConnectProxy(async (host, port, ct) =>
        {
            Interlocked.Increment(ref opened);
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, echoPort, ct);
            return tcp.GetStream();
        });

        using (var blocked = new TcpClient())
        {
            await blocked.ConnectAsync(IPAddress.Loopback, proxy.Port);
            var stream = blocked.GetStream();
            var request = Encoding.ASCII.GetBytes("CONNECT example.test:22 HTTP/1.1\r\nHost: example.test:22\r\n\r\n");
            await stream.WriteAsync(request);
            var response = new byte[64];
            var read = await stream.ReadAsync(response);
            var text = Encoding.ASCII.GetString(response, 0, read);
            Assert.Contains("403", text, StringComparison.Ordinal);
        }

        Assert.Equal(0, opened);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var clientStream = client.GetStream();
        var connect = Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n");
        var combined = new byte[connect.Length + leftover.Length];
        connect.CopyTo(combined, 0);
        leftover.CopyTo(combined, connect.Length);
        await clientStream.WriteAsync(combined);
        var first = new byte[128];
        var firstRead = await clientStream.ReadAsync(first);
        var firstText = Encoding.ASCII.GetString(first, 0, firstRead);
        var headerEnd = firstText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headerEnd >= 0);
        Assert.Contains("200", firstText[..headerEnd], StringComparison.Ordinal);

        var already = firstRead - (headerEnd + 4);
        var echoed = new byte[leftover.Length];
        if (already > 0)
        {
            first.AsSpan(headerEnd + 4, already).CopyTo(echoed);
        }

        if (already < leftover.Length)
        {
            await clientStream.ReadExactlyAsync(echoed.AsMemory(already));
        }

        Assert.Equal(leftover, echoed);
        await echoTask;
        echo.Stop();
    }
}

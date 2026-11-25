using System.Net;
using System.Net.Sockets;
using System.Text;
using VardyParty.LocalService.Compute;

namespace VardyParty.LocalService.Tests;

public sealed class PopProxyDirectTests
{
    [Theory]
    [InlineData("update.googleapis.com")]
    [InlineData("www.google.com")]
    [InlineData("google.com")]
    [InlineData("fonts.gstatic.com")]
    [InlineData("redirector.gvt1.com")]
    [InlineData("www.gstatic.com")]
    [InlineData("pagead2.googlesyndication.com")]
    [InlineData("www.googletagmanager.com")]
    [InlineData("clients2.google.com")]
    [InlineData("safebrowsing.googleapis.com")]
    public void GoogleInfraLeavesWindows(string host)
    {
        Assert.True(PopProxyDirect.ShouldLeaveWindows(host));
    }

    [Theory]
    [InlineData("player.example.test")]
    [InlineData("example.test")]
    [InlineData("statics1.example.test")]
    [InlineData("127.0.0.1")]
    [InlineData("")]
    public void MatchHostsStayOnThePhone(string host)
    {
        Assert.False(PopProxyDirect.ShouldLeaveWindows(host));
    }

    [Fact]
    public async Task Connect_LeavesGoogleInfraOnWindows()
    {
        var phoneOpened = 0;
        var directOpened = 0;
        await using var proxy = new PopConnectProxy(
            (_, _, _) =>
            {
                Interlocked.Increment(ref phoneOpened);
                throw new InvalidOperationException("phone path must not open Google infra");
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref directOpened);
                return Task.FromResult<Stream>(new MemoryStream());
            });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            "CONNECT update.googleapis.com:443 HTTP/1.1\r\nHost: update.googleapis.com:443\r\n\r\n");
        await stream.WriteAsync(request);
        var response = new byte[64];
        var read = await stream.ReadAsync(response);
        var text = Encoding.ASCII.GetString(response, 0, read);

        Assert.Contains("200", text, StringComparison.Ordinal);
        Assert.Equal(0, phoneOpened);
        Assert.Equal(1, directOpened);
    }
}

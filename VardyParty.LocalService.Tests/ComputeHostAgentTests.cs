using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.LocalService.Compute;
using VardyParty.LocalService.Discovery;

namespace VardyParty.LocalService.Tests;

public sealed class ComputeHostAgentTests
{
    [Fact]
    public void SocketClose_DoesNotCompleteLinkWhileDirectIsActive()
    {
        // Arrange
        var links = new ConcurrentDictionary<int, RelayLink>();
        var link = RelayLink.CreateTest(7);
        links[7] = link;
        var close = RelayFrames.Parse("""{"t":"close","id":"7"}""");

        // Act
        ComputeHostAgent.ApplySocketLinkControl(1, close, links);
        var ignoredWhileDirect = link.IncomingCompleted;
        ComputeHostAgent.ApplySocketLinkControl(0, close, links);

        // Assert
        Assert.False(ignoredWhileDirect);
        Assert.True(link.IncomingCompleted);
    }

    [Fact]
    public void GuestPopProxy_IsReplacedByTheHostTunnel()
    {
        // Arrange
        using var request = new HttpRequestMessage();
        using var headers = JsonDocument.Parse("""{"X-Vardy-Pop-Proxy":"http://127.0.0.1:9","X-Other":"yes"}""");

        // Act
        ComputeGuestHeaders.Copy(request, headers.RootElement);
        ComputeGuestHeaders.AssignHostPopProxy(request, "http://127.0.0.1:4321");

        // Assert
        Assert.Equal("http://127.0.0.1:4321", request.Headers.GetValues(PopProxyHeader.Name).Single());
        Assert.Equal("yes", request.Headers.GetValues("X-Other").Single());
        Assert.DoesNotContain(request.Headers, h => h.Key.Equals(PopProxyHeader.Name, StringComparison.OrdinalIgnoreCase)
            && h.Value.Contains("http://127.0.0.1:9"));
    }

    [Fact]
    public void AbortInFlightCompute_CancelsTheRunningMp()
    {
        // Arrange
        using var computeRun = new CancellationTokenSource();

        // Act
        ComputeHostAgent.AbortInFlightCompute(computeRun);

        // Assert
        Assert.True(computeRun.IsCancellationRequested);
    }

    [Fact]
    public void Reconnect_StopsWhenTheUpgradeIsUnauthorized()
    {
        // Arrange
        var unauthorized = new HttpRequestException("upgrade rejected", null, HttpStatusCode.Unauthorized);
        var forbidden = new HttpRequestException("accès refusé", null, HttpStatusCode.Forbidden);
        var socketDenied = new WebSocketException(
            "Unable to connect to the remote server",
            new IOException("An attempt was made to access a socket in a way forbidden by its access permissions."));
        var dropped = new WebSocketException("Unable to connect to the remote server");
        var lookalike = new IOException("waited 401ms");

        // Act
        var retryUnauthorized = ComputeHostAgent.ShouldRetryReconnect(unauthorized);
        var retryForbidden = ComputeHostAgent.ShouldRetryReconnect(forbidden);
        var retrySocketDenied = ComputeHostAgent.ShouldRetryReconnect(socketDenied);
        var retryDropped = ComputeHostAgent.ShouldRetryReconnect(dropped);
        var retryLookalike = ComputeHostAgent.ShouldRetryReconnect(lookalike);

        // Assert
        Assert.False(retryUnauthorized);
        Assert.False(retryForbidden);
        Assert.True(retrySocketDenied);
        Assert.True(retryDropped);
        Assert.True(retryLookalike);
    }

    [Fact]
    public void ReconnectDelay_DoublesUntilFifteenSeconds()
    {
        // Arrange
        var seconds = ComputeHostAgent.ReconnectDelayInitialSeconds;
        var sequence = new List<int> { seconds };

        // Act
        for (var step = 0; step < 4; step++)
        {
            seconds = ComputeHostAgent.NextReconnectDelaySeconds(seconds);
            sequence.Add(seconds);
        }

        // Assert
        Assert.Equal(15, ComputeHostAgent.ReconnectDelayCapSeconds);
        Assert.Equal(new[] { 2, 4, 8, 15, 15 }, sequence);
    }

    [Fact]
    public async Task Reconnect_UnauthorizedUpgradeStopsSharingAndDisposesTheSocket()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "vardy-host-share-" + Guid.NewGuid().ToString("N"));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "VardyParty.LocalService.Tests",
            ContentRootPath = Path.GetTempPath(),
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        var connects = 0;
        app.Run(async context =>
        {
            if (Interlocked.Increment(ref connects) == 1)
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                accepted.TrySetResult();
                await release.Task;
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "drop", CancellationToken.None);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });
        await app.StartAsync();
        var apiBaseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        var store = new HostShareStore(Path.Combine(directory, "host-share.dat"), new ReverseProtector());
        store.Save(new HostShareRecord(apiBaseUrl, "Bearer test-token", "cid-1"));
        var runtime = new DiscoveryRuntimeState { HttpPort = 1 };
        await using var agent = new ComputeHostAgent(runtime, NullLogger<ComputeHostAgent>.Instance, store);

        try
        {
            // Act
            await agent.StartAsync(apiBaseUrl, "Bearer test-token", CancellationToken.None, "cid-1");
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var socket = HostSocket(agent);
            Assert.NotNull(socket);
            release.TrySetResult();
            var stopped = await WaitUntilAsync(() => SocketDisposed(socket) && store.Load() is null);

            // Assert
            Assert.True(stopped);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Text, true, CancellationToken.None));
            Assert.False(agent.IsSharing);
            Assert.Null(store.Load());
            Assert.Equal(2, Volatile.Read(ref connects));
        }
        finally
        {
            release.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void PhoneConnectFailed_DoesNotContainHostOrPort()
    {
        // Arrange
        var message = ComputeHostAgent.PhoneConnectFailed;

        // Act
        // Assert
        Assert.Equal("Phone could not open the CONNECT", message);
        Assert.DoesNotContain(':', message);
        Assert.DoesNotMatch(@":\d+", message);
    }

    private static ClientWebSocket? HostSocket(ComputeHostAgent agent) =>
        (ClientWebSocket?)typeof(ComputeHostAgent)
            .GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent);

    private static bool SocketDisposed(ClientWebSocket socket)
    {
        var state = typeof(ClientWebSocket)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(socket);
        return state?.ToString() == "Disposed";
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private sealed class ReverseProtector : IHostShareProtector
    {
        public byte[] Protect(byte[] plaintext)
        {
            var copy = plaintext.ToArray();
            Array.Reverse(copy);
            return copy;
        }

        public byte[] Unprotect(byte[] protectedPayload)
        {
            var copy = protectedPayload.ToArray();
            Array.Reverse(copy);
            return copy;
        }
    }
}

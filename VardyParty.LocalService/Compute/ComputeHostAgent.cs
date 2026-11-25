using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;
using VardyParty.LocalService.Discovery;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Dials the compute relay as the host. Browser CONNECT calls during a relayed
/// /mp or /play are satisfied by the paired phone.
/// </summary>
public sealed class ComputeHostAgent(
    DiscoveryRuntimeState runtime,
    ILogger<ComputeHostAgent> logger,
    HostShareStore shares) : IAsyncDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _compute = new(1, 1);
    private CancellationTokenSource? _computeRun;
    private readonly object _gate = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _run;
    private Task? _receive;
    private int _nextStreamId;
    private Func<JsonElement, Task>? _onSignal;
    private int _directActive;
    private string? _apiBaseUrl;
    private string? _authorization;
    private string? _correlationId;
    private int _keepAlive;
    private int _generation;
    private int _inviteUsed;

    public bool InviteUsed => Volatile.Read(ref _inviteUsed) == 1;

    public bool IsSharing
    {
        get
        {
            lock (_gate)
            {
                return _socket?.State == WebSocketState.Open;
            }
        }
    }

    public async Task StartAsync(string apiBaseUrl, string authorization, CancellationToken cancellationToken, string? correlationId = null)
    {
        if (runtime.HttpPort <= 0)
        {
            throw new InvalidOperationException("Local service is not listening.");
        }

        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A Bearer token is required to share compute.");
        }

        var cid = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId.Trim();
        Volatile.Write(ref _inviteUsed, 0);
        var generation = Interlocked.Increment(ref _generation);
        lock (_gate)
        {
            _apiBaseUrl = apiBaseUrl;
            _authorization = authorization.Trim();
            _correlationId = cid;
        }

        // Replacing the socket is not an unshare. The generation bump stops the previous session from reconnecting.
        await CloseCurrentAsync().ConfigureAwait(false);

        ClientWebSocket socket;
        try
        {
            socket = await ConnectSocketAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _keepAlive, 0);
            throw;
        }

        Volatile.Write(ref _keepAlive, 1);
        var run = new CancellationTokenSource();
        lock (_gate)
        {
            _socket = socket;
            _run = run;
            _receive = MaintainSessionAsync(socket, generation, run.Token);
        }

        logger.LogInformation("Compute relay {CorrelationId} host connected", cid);
    }

    public async Task StopAsync()
    {
        Volatile.Write(ref _keepAlive, 0);
        Volatile.Write(ref _inviteUsed, 0);
        Interlocked.Increment(ref _generation);
        await CloseCurrentAsync().ConfigureAwait(false);
    }

    private async Task CloseCurrentAsync()
    {
        ClientWebSocket? socket;
        CancellationTokenSource? run;
        Task? receive;
        lock (_gate)
        {
            socket = _socket;
            run = _run;
            receive = _receive;
            _socket = null;
            _run = null;
            _receive = null;
        }

        run?.Cancel();
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Compute relay host close failed");
            }
        }

        socket?.Dispose();
        if (receive is not null)
        {
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }

        run?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private bool IsSessionCurrent(int generation) =>
        Volatile.Read(ref _keepAlive) != 0 && Volatile.Read(ref _generation) == generation;

    private async Task MaintainSessionAsync(ClientWebSocket socket, int generation, CancellationToken cancellationToken)
    {
        var delaySeconds = ReconnectDelayInitialSeconds;
        while (true)
        {
            using var pingStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ping = PingLoopAsync(socket, generation, pingStop.Token);
            ReceiveEnd end;
            try
            {
                end = await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                pingStop.Cancel();
                try
                {
                    await ping.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ping stopped with the socket
                }
            }

            if (end != ReceiveEnd.Dropped || cancellationToken.IsCancellationRequested || !IsSessionCurrent(generation))
            {
                return;
            }

            logger.LogInformation("Compute relay host socket dropped; reconnecting");
            ClientWebSocket? next = null;
            while (IsSessionCurrent(generation) && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                    next = await ConnectSocketAsync(cancellationToken).ConfigureAwait(false);
                    delaySeconds = ReconnectDelayInitialSeconds;
                    break;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!ShouldRetryReconnect(ex))
                    {
                        logger.LogWarning(ex, "Compute relay unauthorized; reconnect stopped until sharing credentials change");
                        AbandonSession(generation);
                        return;
                    }

                    logger.LogWarning(ex, "Compute relay host reconnect failed");
                    delaySeconds = NextReconnectDelaySeconds(delaySeconds);
                }
            }

            if (next is null || !IsSessionCurrent(generation))
            {
                next?.Dispose();
                return;
            }

            var previous = socket;
            lock (_gate)
            {
                if (!IsSessionCurrent(generation))
                {
                    next.Dispose();
                    return;
                }

                _socket = next;
            }

            previous.Dispose();
            socket = next;
        }
    }

    private async Task PingLoopAsync(ClientWebSocket socket, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && IsSessionCurrent(generation) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                if (socket.State != WebSocketState.Open || !IsSessionCurrent(generation))
                {
                    return;
                }

                try
                {
                    await SendTextAsync(socket, new { t = "ping" }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Compute relay host ping failed");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // session replaced or stopped
        }
    }

    private async Task<ClientWebSocket> ConnectSocketAsync(CancellationToken cancellationToken)
    {
        string apiBaseUrl;
        string authorization;
        string correlationId;
        lock (_gate)
        {
            apiBaseUrl = _apiBaseUrl ?? throw new InvalidOperationException("Compute relay host is not configured.");
            authorization = _authorization ?? throw new InvalidOperationException("Compute relay host is not configured.");
            correlationId = _correlationId ?? throw new InvalidOperationException("Compute relay host is not configured.");
        }

        var socket = new ClientWebSocket();
        try
        {
            // The upgrade status lives on the socket. WebSocketException only has a
            // localized message, so copy 401/403 onto HttpRequestException before dispose.
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader("Authorization", authorization);
            await socket.ConnectAsync(RelayUri(apiBaseUrl, correlationId), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex)
        {
            HttpRequestException? rejected = null;
            if (ex is not OperationCanceledException
                && socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                rejected = new HttpRequestException(ex.Message, ex, socket.HttpStatusCode);
            }

            socket.Dispose();
            if (rejected is not null)
            {
                throw rejected;
            }

            throw;
        }
    }

    private async Task<ReceiveEnd> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[RelayFrames.MaxPayload + 64];
        var links = new ConcurrentDictionary<int, RelayLink>();
        var rpcs = new List<Task>();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return ReceiveEnd.Dropped;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var bytes = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (Volatile.Read(ref _directActive) == 0
                        && RelayFrames.TryDecodeData(bytes, out var streamId, out var payload)
                        && links.TryGetValue(streamId, out var link))
                    {
                        link.Push(payload);
                    }

                    continue;
                }

                var text = Encoding.UTF8.GetString(bytes);
                JsonElement frame;
                try
                {
                    frame = RelayFrames.Parse(text);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Ignoring malformed compute relay frame");
                    continue;
                }

                switch (RelayFrames.Kind(frame))
                {
                    case "rpc":
                        rpcs.Add(HandleRpcAsync(socket, frame, links, cancellationToken));
                        break;
                    case "log":
                        break;
                    case "signal":
                        var onSignal = _onSignal;
                        if (onSignal != null)
                        {
                            _ = onSignal(frame);
                        }
                        break;
                    case "opened":
                    case "open-failed":
                    case "close":
                        ApplySocketLinkControl(Volatile.Read(ref _directActive), frame, links);
                        break;
                    case "peer-closed":
                        logger.LogInformation("Compute relay guest disconnected");
                        AbortInFlightCompute(_computeRun);
                        break;
                    case "invite-used":
                        Volatile.Write(ref _inviteUsed, 1);
                        logger.LogInformation("Compute relay invite code was used");
                        break;
                }
            }

            return cancellationToken.IsCancellationRequested ? ReceiveEnd.Canceled : ReceiveEnd.Dropped;
        }
        catch (OperationCanceledException)
        {
            return ReceiveEnd.Canceled;
        }
        catch (WebSocketException ex)
        {
            logger.LogInformation(ex, "Compute relay host socket closed");
            return ReceiveEnd.Dropped;
        }
        catch (ObjectDisposedException)
        {
            return ReceiveEnd.Dropped;
        }
        finally
        {
            // Socket is gone (or stopping). Stop /mp so Chrome does not keep
            // harvesting for a guest that can no longer receive rpc-part.
            AbortInFlightCompute(_computeRun);
            try
            {
                await Task.WhenAll(rpcs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Compute relay rpc cleanup finished with an error");
            }

            foreach (var link in links.Values)
            {
                link.Complete();
            }
        }
    }

    private async Task HandleRpcAsync(
        ClientWebSocket socket,
        JsonElement frame,
        ConcurrentDictionary<int, RelayLink> links,
        CancellationToken cancellationToken)
    {
        var id = RelayFrames.Id(frame);
        var cid = frame.TryGetProperty("cid", out var cidEl) ? cidEl.GetString() : null;
        cid = string.IsNullOrWhiteSpace(cid) ? id : cid.Trim();
        var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _computeRun, mine)?.Cancel();

        var method = "GET";
        var path = "";
        var acquired = false;
        try
        {
            await _compute.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            method = frame.TryGetProperty("method", out var methodEl) ? methodEl.GetString() ?? "GET" : "GET";
            path = frame.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
            if (method is not ("GET" or "POST")
                || (!path.StartsWith("/mp", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("/play/", StringComparison.OrdinalIgnoreCase)))
            {
                await ReportAsync(socket, cid, "warning", "Rejected a path that is not /mp or /play", cancellationToken).ConfigureAwait(false);
                await SendTextAsync(socket, new { t = "rpc-result", id, cid, method, path, status = 400, body = """{"error":"path is not a compute call"}""" }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (mine.IsCancellationRequested)
            {
                return;
            }

            await ReportAsync(socket, cid, "info", $"Relayed {method} {path}", cancellationToken).ConfigureAwait(false);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, mine.Token);
            var pipe = await NegotiatePipeAsync(socket, links, cid, call.Token).ConfigureAwait(false);
            try
            {
            await using var proxy = new PopConnectProxy((host, port, ct) => OpenLinkAsync(pipe, links, cid, host, port, ct));
            using var http = new HttpClient { Timeout = PhonePathBudget.HostHttpTimeout };
            var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{runtime.HttpPort}{path}");
            if (frame.TryGetProperty("headers", out var headers))
            {
                ComputeGuestHeaders.Copy(request, headers);
            }

            ComputeGuestHeaders.AssignHostPopProxy(request, proxy.ProxyUrl);
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = frame.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, call.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (MpNdjsonRelay.IsNdjson(contentType))
            {
                await using var stream = await response.Content.ReadAsStreamAsync(call.Token).ConfigureAwait(false);
                await foreach (var line in MpNdjsonRelay.ReadLinesAsync(stream, call.Token).ConfigureAwait(false))
                {
                    await pipe.SendTextAsync(new { t = "rpc-part", id, cid, method, path, status, body = line }, call.Token)
                        .ConfigureAwait(false);
                }

                await ReportAsync(socket, cid, status is >= 200 and < 300 ? "info" : "warning",
                    $"Relayed {method} {path} finished {status}", cancellationToken).ConfigureAwait(false);
                await pipe.SendTextAsync(new
                {
                    t = "rpc-result",
                    id,
                    cid,
                    method,
                    path,
                    status,
                    body = ""
                }, call.Token).ConfigureAwait(false);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync(call.Token).ConfigureAwait(false);
                await ReportAsync(socket, cid, status is >= 200 and < 300 ? "info" : "warning",
                    $"Relayed {method} {path} finished {status}", cancellationToken).ConfigureAwait(false);
                await pipe.SendTextAsync(new
                {
                    t = "rpc-result",
                    id,
                    cid,
                    method,
                    path,
                    status,
                    body = responseBody
                }, cancellationToken).ConfigureAwait(false);
            }
            }
            finally
            {
                Volatile.Write(ref _directActive, 0);
                if (pipe is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Compute relay {CorrelationId} call failed", cid);
            await ReportAsync(socket, cid, "error", ex.Message, CancellationToken.None).ConfigureAwait(false);
            await SendTextAsync(socket, new { t = "rpc-result", id, cid, method, path, status = 500, body = """{"error":"mp play execution failed"}""" }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _compute.Release();
            }

            Interlocked.CompareExchange(ref _computeRun, null, mine);
            mine.Dispose();
        }
    }

    private async Task<Stream> OpenLinkAsync(
        IFramePipe pipe,
        ConcurrentDictionary<int, RelayLink> links,
        string correlationId,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextStreamId);
        var link = new RelayLink(
            id,
            payload => pipe.SendBinaryAsync(RelayFrames.EncodeData(id, payload), cancellationToken),
            () => pipe.SendTextAsync(new { t = "close", id, cid = correlationId, host, port }, CancellationToken.None));
        links[id] = link;

        await pipe.SendTextAsync(new { t = "open", id, cid = correlationId, host, port }, cancellationToken).ConfigureAwait(false);
        var opened = await link.Opened.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!opened)
        {
            throw new IOException(PhoneConnectFailed);
        }

        return link.Stream;
    }

    private async Task SendTextAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(RelayFrames.Text(payload));
        await SendBinaryRawAsync(socket, bytes, WebSocketMessageType.Text, cancellationToken).ConfigureAwait(false);
    }

    private Task SendBinaryAsync(ClientWebSocket socket, byte[] payload, CancellationToken cancellationToken) =>
        SendBinaryRawAsync(socket, payload, WebSocketMessageType.Binary, cancellationToken);

    private async Task SendBinaryRawAsync(
        ClientWebSocket socket,
        byte[] payload,
        WebSocketMessageType type,
        CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            await socket.SendAsync(payload, type, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task ReportAsync(
        ClientWebSocket socket,
        string correlationId,
        string level,
        string message,
        CancellationToken cancellationToken)
    {
        if (level == "error")
        {
            logger.LogError("Compute relay {CorrelationId} {Message}", correlationId, message);
        }
        else if (level == "warning")
        {
            logger.LogWarning("Compute relay {CorrelationId} {Message}", correlationId, message);
        }
        else
        {
            logger.LogInformation("Compute relay {CorrelationId} {Message}", correlationId, message);
        }

        try
        {
            await SendTextAsync(socket, new { t = "log", cid = correlationId, level, message, source = "local-service" }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Compute relay {CorrelationId} log frame was not sent", correlationId);
        }
    }

    private static Uri RelayUri(string apiBaseUrl, string correlationId)
    {
        var baseUri = new Uri(apiBaseUrl.TrimEnd('/') + "/");
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/compute/relay",
            Query = "role=host&cid=" + Uri.EscapeDataString(correlationId)
        };
        return builder.Uri;
    }

    private async Task<IFramePipe> NegotiatePipeAsync(
        ClientWebSocket socket,
        ConcurrentDictionary<int, RelayLink> links,
        string correlationId,
        CancellationToken cancellationToken)
    {
        WebRtcComputePeer? peer = null;
        var handedOff = false;
        var transportSent = false;
        var signals = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });
        Task? signalLoop = null;
        Action<string>? onDiagnostic = null;
        Action<RTCIceCandidateInit>? onIce = null;
        Action<byte[]>? onReceived = null;
        Action? onClosed = null;

        async Task SendTransportOnce(string mode, CancellationToken ct)
        {
            if (transportSent)
            {
                return;
            }

            await SendTextAsync(socket, new { t = "transport", mode, cid = correlationId }, ct).ConfigureAwait(false);
            transportSent = true;
        }

        try
        {
            peer = await WebRtcComputePeer.CreateOffererAsync().ConfigureAwait(false);
            var live = peer;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _onSignal = frame =>
            {
                signals.Writer.TryWrite(frame);
                return Task.CompletedTask;
            };
            signalLoop = ConsumeSignalsAsync(live, ready, signals.Reader);
            onDiagnostic = line => logger.LogInformation("Compute relay {CorrelationId} direct: {Line}", correlationId, line);
            onIce = candidate =>
            {
                _ = SendTextAsync(socket, new
                {
                    t = "signal",
                    kind = "ice",
                    cid = correlationId,
                    candidate = candidate.candidate,
                    sdpMid = candidate.sdpMid,
                    sdpMLineIndex = candidate.sdpMLineIndex
                }, CancellationToken.None);
            };
            onReceived = data => DeliverDirect(data, links);
            onClosed = () =>
            {
                if (!ready.Task.IsCompleted)
                {
                    ready.TrySetResult(false);
                    return;
                }

                _ = ReportAsync(socket, correlationId, "warning", "Direct channel dropped", CancellationToken.None);
            };
            peer.Diagnostic += onDiagnostic;
            peer.IceCandidate += onIce;
            peer.Received += onReceived;
            peer.Closed += onClosed;

            var sdp = await peer.CreateOfferAsync().ConfigureAwait(false);
            await SendTextAsync(socket, new { t = "signal", kind = "offer", cid = correlationId, sdp }, cancellationToken)
                .ConfigureAwait(false);

            var useDirect = false;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(ComputeTransportMode.ReadyBudgetSeconds));
                useDirect = await ready.Task.WaitAsync(budget.Token).ConfigureAwait(false);
                if (useDirect)
                {
                    await peer.WaitUntilOpenAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                useDirect = false;
            }

            var mode = ComputeTransportMode.Choose(useDirect);
            if (mode == "relay")
            {
                await ReportAsync(socket, correlationId, "warning", $"Direct channel was not ready; using the relay ({peer.Summary})", cancellationToken)
                    .ConfigureAwait(false);
                await SendTransportOnce("relay", cancellationToken).ConfigureAwait(false);
                return new SocketFramePipe(this, socket);
            }

            Volatile.Write(ref _directActive, 1);
            await ReportAsync(socket, correlationId, "info", $"Direct channel is open ({peer.Summary})", cancellationToken)
                .ConfigureAwait(false);
            await SendTransportOnce("webrtc", cancellationToken).ConfigureAwait(false);
            handedOff = true;
            return new DirectFramePipe(this, live);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var unavailable = peer is null
                ? "Direct channel unavailable; using the relay"
                : $"Direct channel unavailable; using the relay ({peer.Summary})";
            await ReportAsync(socket, correlationId, "warning", unavailable, CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                await SendTransportOnce("relay", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception sendEx) when (sendEx is not OperationCanceledException)
            {
                logger.LogDebug(sendEx, "Compute relay transport fallback was not sent");
            }

            return new SocketFramePipe(this, socket);
        }
        finally
        {
            _onSignal = null;
            signals.Writer.TryComplete();
            if (signalLoop is not null)
            {
                try
                {
                    await signalLoop.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // consumer stopped with the peer
                }
            }

            if (!handedOff)
            {
                Volatile.Write(ref _directActive, 0);
                if (peer is not null)
                {
                    if (onDiagnostic is not null)
                    {
                        peer.Diagnostic -= onDiagnostic;
                    }

                    if (onIce is not null)
                    {
                        peer.IceCandidate -= onIce;
                    }

                    if (onReceived is not null)
                    {
                        peer.Received -= onReceived;
                    }

                    if (onClosed is not null)
                    {
                        peer.Closed -= onClosed;
                    }

                    try
                    {
                        await peer.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Compute relay peer dispose failed");
                    }
                }
            }
        }
    }

    internal const string PhoneConnectFailed = "Phone could not open the CONNECT";

    internal const int ReconnectDelayInitialSeconds = 2;

    internal const int ReconnectDelayCapSeconds = 15;

    internal static int NextReconnectDelaySeconds(int seconds) =>
        Math.Min(seconds * 2, ReconnectDelayCapSeconds);

    internal static bool ShouldRetryReconnect(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http && IsRejectedUpgrade(http.StatusCode))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRejectedUpgrade(HttpStatusCode? status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private void AbandonSession(int generation)
    {
        ClientWebSocket? dead;
        lock (_gate)
        {
            if (Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            Volatile.Write(ref _keepAlive, 0);
            dead = _socket;
            _socket = null;
        }

        try
        {
            dead?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Compute relay host socket dispose failed");
        }

        try
        {
            shares.Clear();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saved host sharing could not be cleared after the relay rejected it");
        }
    }

    /// <summary>
    /// Cancel the in-flight localhost <c>/mp</c> when the host socket is gone.
    /// Without this, Chrome keeps clicking chips after the phone has left.
    /// </summary>
    internal static void AbortInFlightCompute(CancellationTokenSource? computeRun) =>
        computeRun?.Cancel();

    internal static bool SocketAppliesLinkControl(int directActive) => directActive == 0;

    internal static void ApplySocketLinkControl(
        int directActive,
        JsonElement frame,
        ConcurrentDictionary<int, RelayLink> links)
    {
        if (!SocketAppliesLinkControl(directActive))
        {
            return;
        }

        ApplyLinkControl(frame, links);
    }

    private static async Task ConsumeSignalsAsync(
        WebRtcComputePeer peer,
        TaskCompletionSource<bool> ready,
        ChannelReader<JsonElement> reader)
    {
        await foreach (var frame in reader.ReadAllAsync().ConfigureAwait(false))
        {
            await ApplySignalAsync(peer, ready, frame).ConfigureAwait(false);
        }
    }

    private static Task ApplySignalAsync(WebRtcComputePeer peer, TaskCompletionSource<bool> ready, JsonElement frame)
    {
        var kind = frame.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
        switch (kind)
        {
            case "answer":
                var sdp = frame.TryGetProperty("sdp", out var sdpEl) ? sdpEl.GetString() ?? "" : "";
                return peer.AcceptAnswerAsync(sdp);
            case "ice":
                var candidate = frame.TryGetProperty("candidate", out var candidateEl) ? candidateEl.GetString() ?? "" : "";
                var mid = frame.TryGetProperty("sdpMid", out var midEl) ? midEl.GetString() : null;
                ushort line = 0;
                if (frame.TryGetProperty("sdpMLineIndex", out var lineEl)
                    && lineEl.TryGetInt32(out var lineNo)
                    && lineNo is >= 0 and <= ushort.MaxValue)
                {
                    line = (ushort)lineNo;
                }

                return peer.AddIceCandidateAsync(candidate, mid, line);
            case "ready":
                ready.TrySetResult(true);
                return Task.CompletedTask;
            case "failed":
                ready.TrySetResult(false);
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private void DeliverDirect(byte[] data, ConcurrentDictionary<int, RelayLink> links)
    {
        if (data.Length == 0 || Volatile.Read(ref _directActive) == 0)
        {
            return;
        }

        if (data[0] == (byte)'{')
        {
            try
            {
                ApplyLinkControl(RelayFrames.Parse(Encoding.UTF8.GetString(data)), links);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Ignoring malformed direct compute frame");
            }

            return;
        }

        if (RelayFrames.TryDecodeData(data, out var streamId, out var payload) && links.TryGetValue(streamId, out var link))
        {
            link.Push(payload);
        }
    }

    private static void ApplyLinkControl(JsonElement frame, ConcurrentDictionary<int, RelayLink> links)
    {
        if (!int.TryParse(RelayFrames.Id(frame), out var streamId) || !links.TryGetValue(streamId, out var link))
        {
            return;
        }

        switch (RelayFrames.Kind(frame))
        {
            case "opened":
                link.SignalOpen(true);
                break;
            case "open-failed":
                link.SignalOpen(false);
                break;
            case "close":
                link.Complete();
                break;
        }
    }

    private Task SendDirectAsync(WebRtcComputePeer peer, byte[] payload, CancellationToken cancellationToken) =>
        SendExclusiveAsync(() =>
        {
            peer.Send(payload);
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task SendExclusiveAsync(Func<Task> send, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await send().ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private enum ReceiveEnd
    {
        Dropped,
        Canceled
    }

    private interface IFramePipe : IAsyncDisposable
    {
        Task SendTextAsync(object payload, CancellationToken cancellationToken);
        Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken);
    }

    private sealed class SocketFramePipe(ComputeHostAgent agent, ClientWebSocket socket) : IFramePipe
    {
        public Task SendTextAsync(object payload, CancellationToken cancellationToken) =>
            agent.SendTextAsync(socket, payload, cancellationToken);

        public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken) =>
            agent.SendBinaryAsync(socket, payload, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DirectFramePipe(ComputeHostAgent agent, WebRtcComputePeer peer) : IFramePipe
    {
        public Task SendTextAsync(object payload, CancellationToken cancellationToken) =>
            agent.SendDirectAsync(peer, Encoding.UTF8.GetBytes(RelayFrames.Text(payload)), cancellationToken);

        public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken) =>
            agent.SendDirectAsync(peer, payload, cancellationToken);

        public async ValueTask DisposeAsync() => await peer.DisposeAsync().ConfigureAwait(false);
    }
}

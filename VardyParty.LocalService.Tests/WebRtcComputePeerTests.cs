using System.Text;
using SIPSorcery.Net;
using VardyParty.LocalService.Compute;

namespace VardyParty.LocalService.Tests;

public class WebRtcComputePeerTests
{
    [Fact]
    public async Task LoopbackChannel_CarriesAnOpenFrameAndABinaryChunk()
    {
        await using var offerer = await WebRtcComputePeer.CreateOffererAsync(useStun: false);
        await using var answerer = WebRtcComputePeer.CreateAnswerer(useStun: false);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var offerIce = new List<RTCIceCandidateInit>();
        var answerIce = new List<RTCIceCandidateInit>();
        var iceGate = new object();
        offerer.IceCandidate += candidate => { lock (iceGate) offerIce.Add(candidate); };
        answerer.IceCandidate += candidate => { lock (iceGate) answerIce.Add(candidate); };

        var offer = await offerer.CreateOfferAsync();
        var answer = await answerer.AcceptOfferAsync(offer);
        await offerer.AcceptAnswerAsync(answer);
        using var pumpStop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!pumpStop.IsCancellationRequested)
            {
                List<RTCIceCandidateInit> fromOffer;
                List<RTCIceCandidateInit> fromAnswer;
                lock (iceGate)
                {
                    fromOffer = offerIce.ToList();
                    offerIce.Clear();
                    fromAnswer = answerIce.ToList();
                    answerIce.Clear();
                }

                foreach (var candidate in fromOffer)
                {
                    await answerer.AddIceCandidateAsync(candidate);
                }

                foreach (var candidate in fromAnswer)
                {
                    await offerer.AddIceCandidateAsync(candidate);
                }

                try
                {
                    await Task.Delay(20, pumpStop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, pumpStop.Token);

        try
        {
        try
        {
            await Task.WhenAll(
                offerer.WaitUntilOpenAsync(TimeSpan.FromSeconds(15), budget.Token),
                answerer.WaitUntilOpenAsync(TimeSpan.FromSeconds(15), budget.Token));
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                $"channel did not open. offerer {offerer.ConnectionState}/{offerer.IceState}/{offerer.ChannelState} answerer {answerer.ConnectionState}/{answerer.IceState}/{answerer.ChannelState} offerHasCandidate={offer.Contains("candidate", StringComparison.OrdinalIgnoreCase)}");
        }

        var openSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunkSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replySeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.Received += data =>
        {
            if (data.Length > 0 && data[0] == (byte)'{')
            {
                openSeen.TrySetResult(Encoding.UTF8.GetString(data));
            }
            else if (RelayFrames.TryDecodeData(data, out var id, out var payload))
            {
                Assert.Equal(7, id);
                chunkSeen.TrySetResult(payload);
            }
        };
        offerer.Received += data =>
        {
            if (RelayFrames.TryDecodeData(data, out var id, out var payload))
            {
                Assert.Equal(7, id);
                replySeen.TrySetResult(payload);
            }
        };

        offerer.Send(Encoding.UTF8.GetBytes("""{"t":"open","id":7,"host":"cdn.example","port":443}"""));
        offerer.Send(RelayFrames.EncodeData(7, "ping"u8.ToArray()));
        answerer.Send(RelayFrames.EncodeData(7, "pong"u8.ToArray()));

        var open = await openSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var chunk = await chunkSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reply = await replySeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"t\":\"open\"", open, StringComparison.Ordinal);
        Assert.Equal("ping", Encoding.ASCII.GetString(chunk));
        Assert.Equal("pong", Encoding.ASCII.GetString(reply));
        }
        finally
        {
            pumpStop.Cancel();
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // pump stopped
            }
        }
    }

    [Theory]
    [InlineData(true, "webrtc")]
    [InlineData(false, "relay")]
    public void Choose_SelectsOneTransport(bool dataChannelReady, string mode)
    {
        Assert.Equal(mode, ComputeTransportMode.Choose(dataChannelReady));
        Assert.True(ComputeTransportMode.ReadyBudgetSeconds > 10, "the host must outlast the phone's 10 s open budget");
    }
}

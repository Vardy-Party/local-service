namespace VardyParty.LocalService.Compute;

internal static class ComputeTransportMode
{
    /// <summary>
    /// How long the host waits for the phone's "ready" signal. This outlasts
    /// the phone's 10 s open budget, so when the channel does not open the
    /// phone's "failed" signal ends the wait first and the fallback report
    /// carries the phone's view as well as the host's.
    /// </summary>
    public const int ReadyBudgetSeconds = 12;

    public static string Choose(bool dataChannelReady) => dataChannelReady ? "webrtc" : "relay";
}

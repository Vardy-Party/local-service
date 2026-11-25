namespace VardyParty.LocalService.Compute;

/// <summary>
/// How long Chrome may take when the page loads through the paired phone.
/// Chip, playlist, and segment waits run one after another.
/// The phone app waits <see cref="GuestFindingStreamsWait"/> and the host HTTP
/// client waits <see cref="HostHttpTimeout"/>, so a slow mobile link finishes
/// inside the phone's finding-streams wait.
/// </summary>
public static class PhonePathBudget
{
    public static readonly TimeSpan ChipWait = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan PlaylistWait = TimeSpan.FromSeconds(180);

    public static readonly TimeSpan SegmentWait = TimeSpan.FromSeconds(60);

    /// <summary>Agreed with the phone client (<c>RemoteComputeHttp.FindingStreamsTimeout</c>) for the first chip.</summary>
    public static readonly TimeSpan GuestFindingStreamsWait = TimeSpan.FromSeconds(420);

    /// <summary>
    /// Host HTTP client stays open for the full chip harvest after the guest
    /// has already returned the first playlist. Eight chips: 360s + 7×30s = 570s.
    /// </summary>
    public static readonly TimeSpan HostHttpTimeout = TimeSpan.FromSeconds(630);

    public static readonly TimeSpan WarmPlaylistWait = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan WarmSegmentWait = TimeSpan.FromSeconds(10);

    public static TimeSpan ChromeSequence => ChipWait + PlaylistWait + SegmentWait;

    public static TimeSpan WarmChipSequence => WarmPlaylistWait + WarmSegmentWait;

    public static TimeSpan HarvestSequence(int chipCount) =>
        ChromeSequence + Math.Max(0, chipCount - 1) * WarmChipSequence;
}

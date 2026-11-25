namespace VardyParty.LocalService.Compute;

/// <summary>
/// Chrome/Google infra that must leave Windows directly so those CONNECTs
/// never occupy the single phone channel. Match and player hosts stay on
/// the phone path.
/// </summary>
public static class PopProxyDirect
{
    public static readonly string[] HostSuffixes =
    [
        ".google.com",
        ".googleapis.com",
        ".gstatic.com",
        ".gvt1.com",
        ".gvt2.com",
        ".googleusercontent.com",
        ".googletagmanager.com",
        ".googletagservices.com",
        ".googleadservices.com",
        ".doubleclick.net",
        ".googlesyndication.com",
        ".google-analytics.com",
        ".chrome.com"
    ];

    public static bool ShouldLeaveWindows(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || System.Net.IPAddress.TryParse(name, out _))
        {
            return false;
        }

        foreach (var suffix in HostSuffixes)
        {
            var apex = suffix[1..];
            if (name.Equals(apex, StringComparison.Ordinal) || name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

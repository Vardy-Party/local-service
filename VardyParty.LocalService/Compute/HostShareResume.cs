namespace VardyParty.LocalService.Compute;

/// <summary>
/// Whether a saved host share should be resumed or forgotten after a restart.
/// </summary>
public static class HostShareResume
{
    public static bool IsUsable(HostShareRecord? record)
    {
        if (record is null
            || string.IsNullOrWhiteSpace(record.ApiBaseUrl)
            || string.IsNullOrWhiteSpace(record.Authorization))
        {
            return false;
        }

        if (!record.Authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Uri.TryCreate(record.ApiBaseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    public static bool ShouldForget(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (MentionsStatus(message, 401)
                || MentionsStatus(message, 403)
                || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MentionsStatus(string message, int code)
    {
        var token = code.ToString();
        return message.Contains($"status code '{token}'", StringComparison.OrdinalIgnoreCase)
            || message.Contains($"status code {token}", StringComparison.OrdinalIgnoreCase)
            || message.Contains($": {token} (", StringComparison.Ordinal);
    }
}

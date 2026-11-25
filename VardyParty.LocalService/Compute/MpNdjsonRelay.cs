using System.Runtime.CompilerServices;
using System.Text;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Line reader for a streaming <c>POST /mp</c> body. Each line is one JSON event.
/// </summary>
internal static class MpNdjsonRelay
{
    public const string ContentType = "application/x-ndjson";
    public const string RequestHeaderName = "X-Vardy-Mp-Stream";

    /// <summary>
    /// One NDJSON event must stay under this many UTF-16 chars so a huge line
    /// cannot fill the host WebSocket as a single <c>rpc-part</c>.
    /// </summary>
    public const int MaxLineLength = 262_144;

    public static bool IsNdjson(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && contentType.Contains("ndjson", StringComparison.OrdinalIgnoreCase);

    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        while (true)
        {
            var line = await ReadBoundedLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            yield return line;
        }
    }

    /// <summary>
    /// Host frames for a streamed <c>/mp</c>: one <c>rpc-part</c> per line, then
    /// an empty <c>rpc-result</c>.
    /// </summary>
    public static IReadOnlyList<(string Type, string Body)> HostRpcSequence(IReadOnlyList<string> lines)
    {
        var frames = new List<(string Type, string Body)>(lines.Count + 1);
        foreach (var line in lines)
        {
            frames.Add(("rpc-part", line));
        }

        frames.Add(("rpc-result", ""));
        return frames;
    }

    internal static async Task<string?> ReadBoundedLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var overflow = false;
        var buffer = new char[1];
        while (true)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            var ch = buffer[0];
            if (ch == '\n')
            {
                break;
            }

            if (ch == '\r')
            {
                continue;
            }

            if (overflow)
            {
                continue;
            }

            if (builder.Length >= MaxLineLength)
            {
                overflow = true;
                continue;
            }

            builder.Append(ch);
        }

        return overflow ? "" : builder.ToString();
    }
}

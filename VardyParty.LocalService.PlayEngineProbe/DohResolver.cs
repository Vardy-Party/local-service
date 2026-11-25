using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Minimal RFC 8484 (wire format, GET ?dns=) DoH resolver for the probe's HttpClient,
/// so Exo-shaped playlist/segment GETs resolve the same way Chromium does with
/// --dns-over-https-templates. The DoH endpoint itself is reached via system DNS
/// (an IP-literal template like https://1.1.1.1/dns-query needs no lookup), matching
/// Chrome's bootstrap behaviour.
/// </summary>
sealed class DohResolver : IDisposable
{
    private readonly string template;
    private readonly HttpClient http;
    private readonly ConcurrentDictionary<string, Task<IPAddress[]>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> reported = new(StringComparer.OrdinalIgnoreCase);

    public DohResolver(string template)
    {
        this.template = template.Trim();
        http = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Template => template;

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return Task.FromResult(new[] { literal });
        return cache.GetOrAdd(host, h => ResolveUncachedAsync(h, ct));
    }

    private async Task<IPAddress[]> ResolveUncachedAsync(string host, CancellationToken ct)
    {
        var a = QueryAsync(host, 1, ct);
        var aaaa = QueryAsync(host, 28, ct);
        var v4 = await a;
        IPAddress[] v6;
        try { v6 = await aaaa; } catch { v6 = []; }
        return [.. v4, .. v6];
    }

    private async Task<IPAddress[]> QueryAsync(string host, ushort qtype, CancellationToken ct)
    {
        var query = BuildQuery(host, qtype);
        var b64 = Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var sep = template.Contains('?') ? '&' : '?';
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{template}{sep}dns={b64}");
        req.Headers.TryAddWithoutValidation("accept", "application/dns-message");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync(ct);
        return ParseAnswers(body, qtype);
    }

    private static byte[] BuildQuery(string host, ushort qtype)
    {
        var ms = new MemoryStream();
        // id=0 (RFC 8484 recommends 0 for cacheability), flags RD, 1 question.
        ms.Write([0, 0, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        ms.Write([(byte)(qtype >> 8), (byte)qtype, 0, 1]);
        return ms.ToArray();
    }

    private static IPAddress[] ParseAnswers(byte[] msg, ushort qtype)
    {
        var span = msg.AsSpan();
        var rcode = span[3] & 0x0F;
        if (rcode != 0)
            throw new IOException($"DoH rcode={rcode}");
        var qd = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            pos = SkipName(span, pos);
            pos += 4;
        }

        var result = new List<IPAddress>();
        for (var i = 0; i < an; i++)
        {
            pos = SkipName(span, pos);
            var type = BinaryPrimitives.ReadUInt16BigEndian(span[pos..]);
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(span[(pos + 8)..]);
            pos += 10;
            if (type == qtype && (rdlen == 4 || rdlen == 16))
                result.Add(new IPAddress(span.Slice(pos, rdlen)));
            pos += rdlen;
        }

        return result.ToArray();
    }

    private static int SkipName(ReadOnlySpan<byte> span, int pos)
    {
        while (true)
        {
            var len = span[pos];
            if (len == 0)
                return pos + 1;
            if ((len & 0xC0) == 0xC0)
                return pos + 2;
            pos += 1 + len;
        }
    }

    /// <summary>SocketsHttpHandler.ConnectCallback: DoH lookup, then a plain TCP connect.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var sw = Stopwatch.StartNew();
        var ips = await ResolveAsync(host, ct);
        var dohMs = sw.ElapsedMilliseconds;
        if (ips.Length == 0)
            throw new HttpRequestException($"DoH ({template}) returned no A/AAAA for {host}");

        // IPv4 first: this host has no IPv6 route guarantee.
        var ordered = ips.OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
        Exception? last = null;
        foreach (var ip in ordered)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var connectSw = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(new IPEndPoint(ip, port), timeout.Token);
                if (reported.TryAdd($"{host}|{ip}|{port}", 0))
                    Console.WriteLine($"  [doh] {host} -> {string.Join(",", ordered.Select(a => a.ToString()))} (doh {dohMs}ms); tcp {ip}:{port} ok in {connectSw.ElapsedMilliseconds}ms");
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [doh] {host} tcp {ip}:{port} FAILED after {connectSw.ElapsedMilliseconds}ms: {ex.Message}");
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException($"DoH connect to {host}:{port} failed", last);
    }

    public void Dispose() => http.Dispose();
}

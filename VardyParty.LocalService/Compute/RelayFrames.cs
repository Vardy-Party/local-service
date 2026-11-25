using System.Buffers.Binary;
using System.Text.Json;

namespace VardyParty.LocalService.Compute;

public static class RelayFrames
{
    public const int MaxPayload = 32 * 1024;

    public static string Text(object payload) => JsonSerializer.Serialize(payload);

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static string Kind(JsonElement message) =>
        message.TryGetProperty("t", out var kind) ? kind.GetString() ?? "" : "";

    public static string Id(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id))
        {
            return "";
        }

        return id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.ToString();
    }

    public static byte[] EncodeData(int streamId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException($"Relay payload exceeds {MaxPayload} bytes.", nameof(payload));
        }

        var buffer = new byte[5 + payload.Length];
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1), (uint)streamId);
        payload.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    public static bool TryDecodeData(ReadOnlySpan<byte> frame, out int streamId, out byte[] payload)
    {
        streamId = 0;
        payload = [];
        if (frame.Length < 5 || frame[0] != 1)
        {
            return false;
        }

        streamId = (int)BinaryPrimitives.ReadUInt32BigEndian(frame[1..]);
        payload = frame[5..].ToArray();
        return true;
    }
}

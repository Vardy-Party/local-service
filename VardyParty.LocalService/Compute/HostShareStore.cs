using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Saved host-share credentials so a restarted local-service can join the
/// relay again without the Windows client. Payload is encrypted for the
/// current user; never log the Bearer token.
/// </summary>
public sealed record HostShareRecord(string ApiBaseUrl, string Authorization, string? CorrelationId);

public sealed class HostShareStore(string filePath, IHostShareProtector protector)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string DefaultFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Path.GetTempPath(), "VardyParty.LocalService");
        }
        else
        {
            root = Path.Combine(root, "VardyParty.LocalService");
        }

        return Path.Combine(root, "host-share.dat");
    }

    public static string DefaultKeyDirectory() =>
        Path.Combine(Path.GetDirectoryName(DefaultFilePath()) ?? Path.GetTempPath(), "keys");

    public string FilePath { get; } = filePath;

    public HostShareRecord? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(FilePath);
            var json = Encoding.UTF8.GetString(protector.Unprotect(protectedBytes));
            var record = JsonSerializer.Deserialize<HostShareRecord>(json, Json);
            return HostShareResume.IsUsable(record) ? record : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void Save(HostShareRecord record)
    {
        if (!HostShareResume.IsUsable(record))
        {
            throw new InvalidOperationException("Host share record is incomplete.");
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            HostShareAcl.RestrictToCurrentUser(directory);
        }

        var json = JsonSerializer.Serialize(record, Json);
        var protectedBytes = protector.Protect(Encoding.UTF8.GetBytes(json));
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        HostShareAcl.RestrictToCurrentUser(temp);
        File.Move(temp, FilePath, overwrite: true);
        HostShareAcl.RestrictToCurrentUser(FilePath);
    }

    public void Clear()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }

        var temp = FilePath + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
    }
}

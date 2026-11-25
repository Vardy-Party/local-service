using System.Text;
using VardyParty.LocalService.Compute;

namespace VardyParty.LocalService.Tests;

public sealed class HostShareStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vardy-host-share-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoadRoundTrip_KeepsApiAndCorrelation()
    {
        var store = CreateStore("host-share.dat");
        store.Save(new HostShareRecord("https://example.test", "Bearer test-token", "cid-1"));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("https://example.test", loaded.ApiBaseUrl);
        Assert.Equal("Bearer test-token", loaded.Authorization);
        Assert.Equal("cid-1", loaded.CorrelationId);
    }

    [Fact]
    public void Save_DoesNotWritePlaintextSecrets()
    {
        var store = CreateStore("host-share.dat");
        store.Save(new HostShareRecord("https://example.test", "Bearer test-token", "cid-1"));

        var bytes = File.ReadAllBytes(store.FilePath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("Bearer test-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cid-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReturnsNullWhenFileIsMissing()
    {
        Assert.Null(CreateStore("missing.dat").Load());
    }

    [Fact]
    public void Load_ReturnsNullWhenThePayloadIsNotProtected()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "host-share.dat");
        File.WriteAllText(path, """{"apiBaseUrl":"https://example.test","authorization":"Bearer test-token"}""");

        Assert.Null(new HostShareStore(path, new ReverseProtector()).Load());
    }

    [Fact]
    public void Clear_RemovesTheSavedShare()
    {
        var store = CreateStore("host-share.dat");
        store.Save(new HostShareRecord("https://example.test", "Bearer test-token", null));

        store.Clear();

        Assert.Null(store.Load());
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void IsUsable_RejectsNonHttpApiBases()
    {
        Assert.False(HostShareResume.IsUsable(new HostShareRecord("ftp://example.test", "Bearer test-token", null)));
        Assert.True(HostShareResume.IsUsable(new HostShareRecord("https://example.test", "Bearer test-token", null)));
    }

    [Fact]
    public void ShouldForget_OnlyWhenTheRelayRejectedTheToken()
    {
        // Arrange
        // Act
        // Assert
        Assert.True(HostShareResume.ShouldForget(new InvalidOperationException("The server returned status code '401'")));
        Assert.True(HostShareResume.ShouldForget(new HttpRequestException("Response status code does not indicate success: 403 (Forbidden).")));
        Assert.False(HostShareResume.ShouldForget(new IOException("connection reset")));
        Assert.False(HostShareResume.ShouldForget(new IOException("waited 401ms")));
        Assert.False(HostShareResume.ShouldForget(new TimeoutException("connect :40123 timed out")));
        Assert.False(HostShareResume.ShouldForget(new HttpRequestException("Name or service not known")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private HostShareStore CreateStore(string name) =>
        new(Path.Combine(_directory, name), new ReverseProtector());

    private sealed class ReverseProtector : IHostShareProtector
    {
        public byte[] Protect(byte[] plaintext)
        {
            var copy = plaintext.ToArray();
            Array.Reverse(copy);
            return copy;
        }

        public byte[] Unprotect(byte[] protectedPayload)
        {
            var copy = protectedPayload.ToArray();
            Array.Reverse(copy);
            return copy;
        }
    }
}

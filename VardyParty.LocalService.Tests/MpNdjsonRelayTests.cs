using System.Text;
using VardyParty.LocalService.Compute;
using Xunit;

namespace VardyParty.LocalService.Tests;

public sealed class MpNdjsonRelayTests
{
    [Fact]
    public void IsNdjson_MatchesStreamingContentType()
    {
        // Arrange
        // Act
        // Assert
        Assert.True(MpNdjsonRelay.IsNdjson("application/x-ndjson"));
        Assert.False(MpNdjsonRelay.IsNdjson("application/json"));
    }

    [Fact]
    public async Task ReadLinesAsync_YieldsEachNonEmptyLine()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"chips\"}\n\n{\"type\":\"chip\"}\n");
        await using var stream = new MemoryStream(bytes);

        // Act
        var lines = new List<string>();
        await foreach (var line in MpNdjsonRelay.ReadLinesAsync(stream, CancellationToken.None))
        {
            lines.Add(line);
        }

        // Assert
        Assert.Equal(2, lines.Count);
        Assert.Contains("chips", lines[0], StringComparison.Ordinal);
        Assert.Contains("chip", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void HostRpcSequence_EmitsPartsThenEmptyResult()
    {
        // Arrange
        var lines = new[] { """{"type":"chips"}""", """{"type":"chip"}""" };

        // Act
        var frames = MpNdjsonRelay.HostRpcSequence(lines);

        // Assert
        Assert.Equal(3, frames.Count);
        Assert.Equal("rpc-part", frames[0].Type);
        Assert.Equal(lines[0], frames[0].Body);
        Assert.Equal("rpc-part", frames[1].Type);
        Assert.Equal(lines[1], frames[1].Body);
        Assert.Equal("rpc-result", frames[2].Type);
        Assert.Equal("", frames[2].Body);
    }

    [Fact]
    public async Task ReadLinesAsync_SkipsALineOverTheCap()
    {
        // Arrange
        var huge = new string('x', MpNdjsonRelay.MaxLineLength + 8);
        var bytes = Encoding.UTF8.GetBytes($"{huge}\n{{\"type\":\"chip\"}}\n");
        await using var stream = new MemoryStream(bytes);

        // Act
        var lines = new List<string>();
        await foreach (var line in MpNdjsonRelay.ReadLinesAsync(stream, CancellationToken.None))
        {
            lines.Add(line);
        }

        // Assert
        Assert.Single(lines);
        Assert.Contains("chip", lines[0], StringComparison.Ordinal);
    }
}

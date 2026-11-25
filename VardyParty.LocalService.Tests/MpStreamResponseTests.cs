using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VardyParty.LocalService.Compute;

namespace VardyParty.LocalService.Tests;

public sealed class MpStreamResponseTests
{
    [Fact]
    public async Task CompleteFailureAsync_BeforeWrite_ReturnsJson500()
    {
        // Arrange
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Act
        var result = await MpStreamResponse.CompleteFailureAsync(http, wrote: false, jsonOptions, CancellationToken.None);

        // Assert
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(((MemoryStream)http.Response.Body).ToArray());
    }

    [Fact]
    public async Task CompleteFailureAsync_AfterWrite_StaysNdjson()
    {
        // Arrange
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Response.ContentType = MpNdjsonRelay.ContentType;
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await http.Response.WriteAsync("""{"type":"chips"}""" + "\n");

        // Act
        var result = await MpStreamResponse.CompleteFailureAsync(http, wrote: true, jsonOptions, CancellationToken.None);

        // Assert
        Assert.False(result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status500InternalServerError });
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("chips", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"error\"", body, StringComparison.Ordinal);
        Assert.Contains('\n', body);
    }
}

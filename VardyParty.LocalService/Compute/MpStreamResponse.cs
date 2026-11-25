using System.Text.Json;
using VardyParty.LocalService.Abstractions.Models;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// How <c>POST /mp</c> finishes after an exception. Once NDJSON has started,
/// the body must stay NDJSON.
/// </summary>
internal static class MpStreamResponse
{
    public const string ExecutionFailed = "mp play execution failed";

    public static async Task<IResult> CompleteFailureAsync(
        HttpContext http,
        bool wrote,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        if (!wrote)
        {
            return Results.Json(new { error = ExecutionFailed },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        try
        {
            await http.Response.WriteAsync(
                    JsonSerializer.Serialize(MpPlayEvent.Fail(500, ExecutionFailed), jsonOptions) + "\n",
                    cancellationToken)
                .ConfigureAwait(false);
            await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (http.RequestAborted.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            // guest already left
        }

        return Results.Empty;
    }
}

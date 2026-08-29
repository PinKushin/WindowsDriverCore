using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>One entry of <c>POST /log</c>.</summary>
/// <param name="Timestamp">Unix milliseconds.</param>
/// <param name="Level">Always <c>INFO</c>.</param>
/// <param name="Message">The transcript line.</param>
public sealed record LogRecord(
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// <c>GET /log/types</c> and <c>POST /log</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>WinAppDriver serves neither</b> — measured 2026-08-29, both answer 404 —
/// so this is the *plus more* half of the goal. It is also the one place this
/// driver has clearly more to offer than the reference: the transcript records
/// the work behind each request, with a find's match count and a click's chosen
/// strategy, and until now none of it was reachable except by reading the
/// server's console.
/// </para>
/// <para>
/// <b>One type, <c>server</c>.</b> The protocol lets a driver name several
/// (<c>browser</c>, <c>driver</c>, <c>performance</c>); this has one source of
/// truth and offering aliases for it would suggest a client could get different
/// content by asking differently.
/// </para>
/// <para>
/// <b>The log DRAINS, which is the protocol's contract.</b> Each call returns
/// entries since the last one. A client polls this, and one that re-read its
/// whole history every time would report every line again on every call.
/// </para>
/// </remarks>
public static class LogRoutes
{
    /// <summary>The only log type this driver has.</summary>
    public const string ServerLog = "server";

    /// <summary>Maps the log routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapLogRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/session/{sessionId}/log/types", (HttpContext context) =>
        {
            DriverSession session = context.GetSession();

            return Results.Json(JsonWireResponse.ForSession(
                session.Id, new List<string> { ServerLog }));
        }).RequiresSession();

        app.MapPost("/session/{sessionId}/log", async (HttpContext context, LogBuffer? buffer) =>
        {
            DriverSession session = context.GetSession();

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);

            string? type = body.RootElement.TryGetProperty("type", out JsonElement value)
                ? value.GetString()
                : null;

            // A TYPE THIS DRIVER DOES NOT HAVE IS REFUSED, not answered with the
            // server log. Handing back the wrong log for "performance" would have
            // a client draw conclusions from timings that are request records.
            if (!string.Equals(type, ServerLog, StringComparison.Ordinal))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.InvalidArgument,
                        $"Unknown log type '{type}'. This driver has one: '{ServerLog}'"),
                    statusCode: WebDriverFault.InvalidArgument.HttpStatus);
            }

            if (buffer is null)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownError,
                        "No log buffer is registered on this server"),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            }

            List<LogRecord> records = [];

            foreach (LogEntry entry in buffer.Drain())
            {
                records.Add(new LogRecord(entry.Timestamp, entry.Level, entry.Message));
            }

            return Results.Json(JsonWireResponse.ForSession(session.Id, records));
        }).RequiresSession();

        return app;
    }
}

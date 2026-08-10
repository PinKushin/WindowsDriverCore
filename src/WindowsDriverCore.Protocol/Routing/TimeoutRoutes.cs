using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>POST /session/{id}/timeouts</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The single largest blocker this driver had.</b> Measured 2026-08-10
/// against the compatibility suite in a Windows 10 guest: 167 of 271 failures
/// were "Command not recognized: POST: /session/{id}/timeouts". The suite sets
/// an implicit wait immediately after creating a session, so every fixture died
/// here before reaching an assertion.
/// </para>
/// <para>
/// The behaviour comes from
/// <c>Recordings/winappdriver-responses.json</c> — the JSON Wire Protocol as the
/// real server implements it, not the W3C shape, which disagrees.
/// </para>
/// </remarks>
public static class TimeoutRoutes
{
    private const string ImplicitType = "implicit";
    private const string PageLoadType = "page load";
    private const string ScriptType = "script";

    /// <summary>Maps the timeout routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapTimeoutRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/timeouts", async (HttpContext context) =>
        {
            DriverSession session = context.GetSession();

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body)
                .ConfigureAwait(false);

            string? type = body.RootElement.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString()
                : null;

            double milliseconds =
                body.RootElement.TryGetProperty("ms", out JsonElement msElement) &&
                msElement.ValueKind == JsonValueKind.Number
                    ? msElement.GetDouble()
                    : -1;

            // Not supported rather than accepted-and-ignored, and that is the
            // honest answer rather than an imitation of WinAppDriver. This driver
            // has no navigation and no script execution, so there is nothing for
            // either timeout to govern; storing one and never applying it would
            // be reporting success for doing nothing. When navigation lands, this
            // becomes wrong and changes.
            //
            // Plain text, not a JSON envelope — measured, and a client parsing
            // the body would break on a helpfully-wrapped one.
            if (type is PageLoadType or ScriptType)
            {
                return Results.Text(
                    $"Unimplemented Command: {type} timeout type is not supported",
                    statusCode: 501);
            }

            if (type != ImplicitType || milliseconds < 0)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.InvalidArgument,
                        $"Bad Command Parameter: ms:{FormatMilliseconds(milliseconds)}, type:{type}"),
                    statusCode: WebDriverFault.InvalidArgument.HttpStatus);
            }

            session.ImplicitWait = TimeSpan.FromMilliseconds(milliseconds);

            return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
        }).RequiresSession();

        return app;
    }

    /// <summary>Formats ms the way the recorded message does.</summary>
    /// <remarks>
    /// The recording says <c>ms:-1</c>, not <c>ms:-1.0</c>, so a whole number
    /// prints without a decimal part. A client matching on this string is doing
    /// something unwise, but the recording is the contract.
    /// </remarks>
    private static string FormatMilliseconds(double milliseconds)
    {
        // Not `milliseconds == Math.Floor(...)`: that is a floating point
        // equality check and the analyzer is right to reject it. Round-tripping
        // through long asks the same question without one.
        long whole = (long)milliseconds;
        return Math.Abs(milliseconds - whole) < double.Epsilon
            ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

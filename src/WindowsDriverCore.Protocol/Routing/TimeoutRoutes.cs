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

            // The dialects disagree about the REQUEST here, not only about the
            // reply: W3C names the timeout as the property and drops `type`
            // entirely. Read as JSON Wire, {"implicit":5000} has no type at all
            // and comes out as a bad parameter — so a Selenium 4 client cannot
            // set an implicit wait, which is close to the first thing it does.
            //
            // Only consulted when `type` is absent, so a JSON Wire body takes
            // exactly the path it always took.
            if (type is null)
            {
                (type, milliseconds) = ReadW3CTimeout(body.RootElement, milliseconds);
            }

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

    /// <summary>Reads a W3C timeouts body into the JSON Wire pair.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="fallback">The milliseconds to keep when nothing is named.</param>
    /// <returns>The timeout type and value, translated.</returns>
    /// <remarks>
    /// <para>
    /// <b><c>implicit</c> wins when several are present.</b> A client may send
    /// all three in one request, and refusing the whole body because it mentions
    /// <c>pageLoad</c> would leave that client unable to set an implicit wait at
    /// all. The 501 is for a request that asks ONLY for something unsupported —
    /// which is still an honest refusal rather than a silent acceptance, and is
    /// the same answer the JSON Wire spelling gets.
    /// </para>
    /// <para>
    /// The names are mapped back to the JSON Wire ones so the refusal message
    /// reads <c>page load</c> either way. A message that changed with the
    /// dialect would be one more thing for a caller to special-case.
    /// </para>
    /// </remarks>
    private static (string? Type, double Milliseconds) ReadW3CTimeout(
        JsonElement body, double fallback)
    {
        if (body.TryGetProperty(ImplicitType, out JsonElement implicitMs) &&
            implicitMs.ValueKind == JsonValueKind.Number)
        {
            return (ImplicitType, implicitMs.GetDouble());
        }

        if (body.TryGetProperty("pageLoad", out _))
        {
            return (PageLoadType, fallback);
        }

        return body.TryGetProperty(ScriptType, out _)
            ? (ScriptType, fallback)
            : (null, fallback);
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

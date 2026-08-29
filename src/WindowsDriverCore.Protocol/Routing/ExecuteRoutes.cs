using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>POST /session/{id}/execute</c> — the <c>windows:</c> vendor commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, because this repository asserted the opposite without checking.</b>
/// Probed 2026-08-29 against WinAppDriver 1.2.2009: <c>POST /execute</c> answers
/// <b>501</b> for every script — <c>windows: click</c>, <c>windows: keys</c>, an
/// empty one — while an invented route answers 404. So the reference ROUTES the
/// command and implements nothing.
/// </para>
/// <para>
/// The vocabulary belongs to <b>appium-windows-driver</b>, the Node driver that
/// wraps WinAppDriver, and that is the client population this serves. Serving it
/// is going beyond the reference rather than closing a gap against it — a 501 is
/// a limitation, not a contract.
/// </para>
/// <para>
/// <b>Both spellings.</b> JSON Wire is <c>/execute</c>; W3C renamed it
/// <c>/execute/sync</c>. The reference serves neither usefully and answers 404
/// to the second, so a Selenium 4 client had nowhere to go at all.
/// </para>
/// <para>
/// <b><c>/execute_async</c> and <c>/execute/async</c> are NOT served.</b> There is
/// nothing asynchronous to run — a vendor command is a synchronous act on the
/// desktop — so accepting one would mean promising a callback that never comes.
/// The unknown-command fallback says so, which is more than the reference's 404
/// conveys.
/// </para>
/// </remarks>
public static class ExecuteRoutes
{
    /// <summary>Maps the execute routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapExecuteRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/execute", Execute).RequiresSession();
        app.MapPost("/session/{sessionId}/execute/sync", Execute).RequiresSession();

        return app;
    }

    private static async Task<IResult> Execute(HttpContext context, VendorCommandRunner? vendor)
    {
        DriverSession session = context.GetSession();

        if (vendor is null)
        {
            return Results.Json(
                JsonWireResponse.ForFault(
                    WebDriverFault.UnknownError,
                    "No vendor command runner is registered on this server"),
                statusCode: WebDriverFault.UnknownError.HttpStatus);
        }

        using JsonDocument body = await JsonDocument
            .ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);

        string? script = body.RootElement.TryGetProperty("script", out JsonElement value)
            ? value.GetString()
            : null;

        // THE FIRST ARGUMENT, which is where every appium-windows-driver command
        // puts its whole payload. Absent is legal - `windows: click` with no
        // arguments clicks where the pointer already is.
        JsonElement? argument = body.RootElement.TryGetProperty("args", out JsonElement args) &&
                                args.ValueKind == JsonValueKind.Array &&
                                args.GetArrayLength() > 0
            ? args[0]
            : null;

        VendorOutcome outcome = vendor.Run(script, argument, session);

        if (outcome.Refusal is not null)
        {
            // InvalidArgument rather than UnknownCommand: the ROUTE is served and
            // the request reached it. Answering unknown-command would tell the
            // client to stop using /execute, when the actual problem is the
            // script it named.
            return Results.Json(
                JsonWireResponse.ForFault(WebDriverFault.InvalidArgument, outcome.Refusal),
                statusCode: WebDriverFault.InvalidArgument.HttpStatus);
        }

        // A vendor command is dispatched input like any other, so a read that
        // follows one waits for the application. The /touch routes learned this
        // the hard way - they were the last input path that did not say so.
        session.InputPending = true;

        return Results.Json(JsonWireResponse.ForSession(session.Id, outcome.Value));
    }
}

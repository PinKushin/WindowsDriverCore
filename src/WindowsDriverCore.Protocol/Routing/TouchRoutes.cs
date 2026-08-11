using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// The JSON Wire Protocol's <c>/touch/*</c> commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wrappers over <see cref="PointerActionRunner"/>, not a second
/// implementation.</b> W3C put every input primitive in <c>/actions</c>, so these
/// routes translate their arguments into the same injection path rather than
/// growing their own. Two input paths that must be kept in step is how
/// WinAppDriver's own XPath singular and plural handling drifted apart into
/// issue #1079, and this is the same shape of hazard.
/// </para>
/// <para>
/// W3C removed these commands and Selenium 4 no longer sends them. They stay
/// because the compatibility suite is JSON Wire and every existing suite in the
/// world speaks it — which is this project's floor.
/// </para>
/// <para>
/// <b>A long press really holds.</b> An application separates a tap from a long
/// press by duration, so <c>longclick</c> keeps the contact down while reporting
/// frames. That is the operation the caller asked for rather than a wait for
/// something to happen, which is the sleeping this project bans.
/// </para>
/// </remarks>
public static class TouchRoutes
{
    /// <summary>How long a long press holds. WinAppDriver's own duration.</summary>
    private static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(1000);

    /// <summary>A tap is a contact that is down only momentarily.</summary>
    private static readonly TimeSpan Tap = TimeSpan.FromMilliseconds(30);

    private const string NoInjector =
        "Unimplemented Command: no pointer injector is registered on this server";

    /// <summary>Maps the touch routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapTouchRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapElementGesture(app, "click", Tap);
        MapElementGesture(app, "longclick", LongPress);

        // Two taps in quick succession. The gap is the system's to interpret:
        // Windows decides what counts as a double tap from its own double-click
        // time, so the driver's job is to deliver two contacts promptly and not
        // to second-guess the threshold.
        app.MapPost("/session/{sessionId}/touch/doubleclick",
            async (HttpContext context, PointerActionRunner? runner) =>
        {
            (int x, int y, string? failure, DriverSession session) =
                await ResolveElement(context, runner).ConfigureAwait(false);

            if (failure is not null)
            {
                return Fault(failure);
            }

            string? first = runner!.Tap(x, y, Tap);
            string? second = first ?? runner.Tap(x, y, Tap);

            return second is null
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : Fault(second);
        }).RequiresSession();

        // scroll and flick are both a held contact dragged by an offset. They
        // differ only in what the application infers from the speed, which is a
        // property of the gesture it receives rather than of the command name.
        MapDrag(app, "scroll");
        MapDrag(app, "flick");

        return app;
    }

    private static void MapElementGesture(
        IEndpointRouteBuilder app, string suffix, TimeSpan hold)
    {
        app.MapPost($"/session/{{sessionId}}/touch/{suffix}",
            async (HttpContext context, PointerActionRunner? runner) =>
        {
            (int x, int y, string? failure, DriverSession session) =
                await ResolveElement(context, runner).ConfigureAwait(false);

            if (failure is not null)
            {
                return Fault(failure);
            }

            string? performed = runner!.Tap(x, y, hold);

            return performed is null
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : Fault(performed);
        }).RequiresSession();
    }

    private static void MapDrag(IEndpointRouteBuilder app, string suffix)
    {
        app.MapPost($"/session/{{sessionId}}/touch/{suffix}",
            async (HttpContext context, PointerActionRunner? runner) =>
        {
            DriverSession session = context.GetSession();

            if (runner is null)
            {
                return Results.Text(NoInjector, statusCode: 501);
            }

            TouchRequest? request = await Read(context).ConfigureAwait(false);

            (int x, int y, string? failure) = request?.Element is { Length: > 0 } element
                ? runner.CentreOf(session.WindowHandle, element)
                : (0, 0, "A touch drag needs an element to start from");

            if (failure is not null)
            {
                return Fault(failure);
            }

            string? performed = runner.Drag(
                x, y, x + (request?.XOffset ?? 0), y + (request?.YOffset ?? 0));

            return performed is null
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : Fault(performed);
        }).RequiresSession();
    }

    private static async Task<(int X, int Y, string? Failure, DriverSession Session)>
        ResolveElement(HttpContext context, PointerActionRunner? runner)
    {
        DriverSession session = context.GetSession();

        if (runner is null)
        {
            return (0, 0, NoInjector, session);
        }

        TouchRequest? request = await Read(context).ConfigureAwait(false);

        if (request?.Element is not { Length: > 0 } element)
        {
            return (0, 0, "A touch command needs an element", session);
        }

        (int x, int y, string? failure) = runner.CentreOf(session.WindowHandle, element);
        return (x, y, failure, session);
    }

    private static async Task<TouchRequest?> Read(HttpContext context)
    {
        try
        {
            return await context.Request
                .ReadFromJsonAsync<TouchRequest>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed body is a caller error, reported as one rather than
            // becoming a 500 from an unhandled exception.
            return null;
        }
    }

    private static IResult Fault(string message) =>
        message == NoInjector
            ? Results.Text(NoInjector, statusCode: 501)
            : Results.Json(
                JsonWireResponse.ForFault(WebDriverFault.UnknownError, message),
                statusCode: WebDriverFault.UnknownError.HttpStatus);

    /// <summary>The body every touch command takes.</summary>
    /// <param name="Element">The element to act on.</param>
    /// <param name="XOffset">Horizontal distance, for scroll and flick.</param>
    /// <param name="YOffset">Vertical distance, for scroll and flick.</param>
    private sealed record TouchRequest(
        [property: JsonPropertyName("element")] string? Element,
        [property: JsonPropertyName("xoffset")] int XOffset,
        [property: JsonPropertyName("yoffset")] int YOffset);
}

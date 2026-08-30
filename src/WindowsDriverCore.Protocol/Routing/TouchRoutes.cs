using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Windows;
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
/// <para>
/// <b>A dead element is answered by <see cref="ElementFault"/>, not here.</b>
/// Three <c>*Error_StaleElement</c> tests compare the message character for
/// character, and these routes used to invent their own. Stale versus
/// never-issued depends on what this server handed out, so the decision belongs
/// where that record lives.
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

    private const string NeedsAnElement = "A touch command needs an element";

    /// <summary>Maps the touch routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapTouchRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // down/move/up: one contact phase per request, so a gesture spans
        // several HTTP calls. Absent before this and answering 404 jwp 9 twice
        // in the last measured run.
        MapContact(app, "down", SyntheticContactPhase.Down);
        MapContact(app, "move", SyntheticContactPhase.Update);
        MapContact(app, "up", SyntheticContactPhase.Up);

        MapElementGesture(app, "click", Tap);
        MapElementGesture(app, "longclick", LongPress);

        // Two taps in quick succession, with no separation between them beyond
        // loop overhead.
        //
        // UNVERIFIED, AND MARKED AS SUCH BECAUSE THIS TEST IS FAILING. The
        // reasoning here used to read as settled — "the gap is the system's to
        // interpret, so the driver's job is to deliver two contacts promptly and
        // not second-guess the threshold". That is an assumption about the touch
        // stack, stated as a fact, and a claim in this repository is not
        // evidence (docs/FOUNDING-PREMISE.md).
        //
        // Windows pairs two contacts into a double tap only inside the
        // double-click TIME and RECTANGLE. The position is identical here, so
        // timing is what is left — and nothing establishes that the stack cannot
        // also fail to pair two contacts that arrive too CLOSE together, which
        // would mean the defect is being too fast rather than too slow.
        //
        // TouchDoubleTap fails in every run.
        // tools/vm/probe-what-gap-makes-a-double-tap.ps1 sweeps the separation
        // from the client through /actions, with a single tap as the control.
        // Until it has run, this comment is a question rather than a rationale.
        app.MapPost("/session/{sessionId}/touch/doubleclick",
            async (
                HttpContext context,
                PointerActionRunner? runner,
                IElementRegistry registry,
                IWindowLocator windows,
                IPointerLog log) =>
        {
            long began = Stopwatch.GetTimestamp();

            DriverSession session = context.GetSession();

            if (runner is null)
            {
                return Results.Text(NoInjector, statusCode: 501);
            }

            (int x, int y, PointerRefusal? failure) =
                await ResolveElement(context, runner).ConfigureAwait(false);

            // See MapElementGesture for why this line exists. TouchDoubleTap is
            // the test that needs it: 200 in 61 ms, and no maximize.
            log.PointerTargeted("touch doubleclick", x, y, -1, -1, Elapsed(began));

            if (failure is null)
            {
                failure = runner.Tap(x, y, Tap) ?? runner.Tap(x, y, Tap);
            }

            if (failure is null)
            {
                // A GESTURE IS DISPATCHED INPUT LIKE ANY OTHER, and these routes
                // never said so - only the keyboard, mouse and element-action
                // routes did. A read that follows a tap or a drag therefore
                // never waited for the application at all.
                session.InputPending = true;
            }

            return failure is null
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : Fault(failure, session, registry, windows);
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
            async (
                HttpContext context,
                PointerActionRunner? runner,
                IElementRegistry registry,
                IWindowLocator windows,
                IPointerLog log) =>
        {
            long began = Stopwatch.GetTimestamp();

            DriverSession session = context.GetSession();

            if (runner is null)
            {
                return Results.Text(NoInjector, statusCode: 501);
            }

            (int x, int y, PointerRefusal? failure) =
                await ResolveElement(context, runner).ConfigureAwait(false);

            // WHERE THE GESTURE AIMED, WRITTEN DOWN BEFORE IT IS DISPATCHED.
            //
            // IPointerLog exists for exactly this case and was wired into the
            // mouse routes only. Its own contract says so: "two 200s and no
            // effect is the case this exists for". TouchDoubleTap is that
            // symptom precisely - the route answers 200 in 61 ms and the window
            // does not maximize - and the transcript could say the gesture was
            // dispatched and nothing about where it went.
            //
            // Measured 2026-08-30: the same route DOES maximize the window when
            // driven directly, twice, with a single tap as the control. So the
            // difference is context, and context is what a coordinate shows.
            log.PointerTargeted($"touch {suffix}", x, y, -1, -1, Elapsed(began));

            failure ??= runner.Tap(x, y, hold);

            if (failure is null)
            {
                // A GESTURE IS DISPATCHED INPUT LIKE ANY OTHER, and these routes
                // never said so - only the keyboard, mouse and element-action
                // routes did. A read that follows a tap or a drag therefore
                // never waited for the application at all.
                session.InputPending = true;
            }

            return failure is null
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : Fault(failure, session, registry, windows);
        }).RequiresSession();
    }

    /// <summary>Where a drag or flick begins.</summary>
    /// <param name="runner">Resolves elements and the pointer's position.</param>
    /// <param name="window">The session window.</param>
    /// <param name="request">The body, which may name an element or neither.</param>
    /// <param name="anonymousFlick">Whether this is the velocity-only form.</param>
    /// <returns>The point, or why it could not be established.</returns>
    /// <remarks>
    /// Three cases and they are genuinely different: an element flick or scroll
    /// starts at the element's centre, an anonymous flick starts wherever the
    /// pointer already is, and anything else has not said where to begin.
    /// </remarks>
    private static (int X, int Y, PointerRefusal? Failure) StartingPoint(
        PointerActionRunner runner, nint window, TouchRequest? request, bool anonymousFlick)
    {
        if (request?.Element is { Length: > 0 } element)
        {
            return runner.CentreOf(window, element);
        }

        return anonymousFlick
            ? runner.WhereThePointerIs(window)
            : (0, 0, PointerRefusal.Reason("A touch drag needs an element to start from"));
    }

    /// <summary>How long an anonymous flick's stated velocity is applied for.</summary>
    /// <remarks>
    /// <b>The one thing the driver must choose on this route.</b> JSON Wire's
    /// anonymous flick gives a speed in pixels per second and no duration, so a
    /// distance only exists once a time is picked. A tenth of a second is a
    /// flick's worth: the suite's own <c>Flick(0, 180)</c> then travels 18 px,
    /// and its comment says "good value typically goes around 160 - 200 pixels
    /// with diminishing delta on the bigger values" — which is about the speed,
    /// not the distance.
    /// </remarks>
    private const double AnonymousFlickSeconds = 0.1;

    private static void MapDrag(IEndpointRouteBuilder app, string suffix)
    {
        app.MapPost($"/session/{{sessionId}}/touch/{suffix}",
            async (
                HttpContext context,
                PointerActionRunner? runner,
                IElementRegistry registry,
                IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            if (runner is null)
            {
                return Results.Text(NoInjector, statusCode: 501);
            }

            TouchRequest? request = await Read(context).ConfigureAwait(false);

            // THE ANONYMOUS FLICK CARRIES NO ELEMENT AND NO OFFSETS - only a
            // velocity per axis, and it flicks from wherever the pointer is.
            // The suite sends exactly that: touchScreen.Flick(0, 180). Requiring
            // an element here refused it outright.
            bool anonymousFlick =
                request?.Element is not { Length: > 0 } &&
                (request?.XSpeed is not null || request?.YSpeed is not null);

            (int x, int y, PointerRefusal? failure) = StartingPoint(
                runner, session.WindowHandle, request, anonymousFlick);

            if (failure is null)
            {
                // OFFSETS FROM THE OFFSETS, or from the velocity when that is
                // all the caller gave. JSON Wire's anonymous flick states pixels
                // per second and leaves the duration to the driver, so the
                // distance is the speed applied for one flick's worth of time.
                (int dx, int dy) = anonymousFlick
                    ? ((int)((request?.XSpeed ?? 0) * AnonymousFlickSeconds),
                       (int)((request?.YSpeed ?? 0) * AnonymousFlickSeconds))
                    : (request?.XOffset ?? 0, request?.YOffset ?? 0);

                // AND THE SPEED IS THE CALLER'S WHEN THEY GAVE ONE. Only when
                // they did not does the driver choose - which is every
                // /touch/scroll, since JSON Wire gives scroll no speed at all.
                failure = runner.Drag(x, y, x + dx, y + dy, request?.Speed);
            }

            if (failure is null)
            {
                // A GESTURE IS DISPATCHED INPUT LIKE ANY OTHER, and these routes
                // never said so - only the keyboard, mouse and element-action
                // routes did. A read that follows a tap or a drag therefore
                // never waited for the application at all.
                session.InputPending = true;
            }

            return failure is null
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : Fault(failure, session, registry, windows);
        }).RequiresSession();
    }

    private static double Elapsed(long began) =>
        Stopwatch.GetElapsedTime(began).TotalMilliseconds;

    private static async Task<(int X, int Y, PointerRefusal? Failure)> ResolveElement(
        HttpContext context, PointerActionRunner runner)
    {
        DriverSession session = context.GetSession();
        TouchRequest? request = await Read(context).ConfigureAwait(false);

        return request?.Element is { Length: > 0 } element
            ? runner.CentreOf(session.WindowHandle, element)
            : (0, 0, PointerRefusal.Reason(NeedsAnElement));
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

    /// <summary>Turns a refusal into the response WinAppDriver would have sent.</summary>
    /// <remarks>
    /// A failed element read is delegated, because stale versus never-issued is
    /// decided from the record of ids this server handed out and nowhere else.
    /// Everything left is genuinely this layer's to explain.
    /// </remarks>
    private static IResult Fault(
        PointerRefusal refusal,
        DriverSession session,
        IElementRegistry registry,
        IWindowLocator windows) =>
        refusal.ElementOutcome is { } outcome && refusal.ElementId is { } elementId
            ? ElementFault.For(outcome, session, elementId, registry, windows)
            : Results.Json(
                JsonWireResponse.ForFault(WebDriverFault.UnknownError, refusal.Message),
                statusCode: WebDriverFault.UnknownError.HttpStatus);

    /// <summary>The body every touch command takes.</summary>
    /// <param name="Element">The element to gesture on, when there is one.</param>
    /// <param name="XOffset">Horizontal distance, for scroll and the element flick.</param>
    /// <param name="YOffset">Vertical distance, for scroll and the element flick.</param>
    /// <param name="Speed">Pixels per second, for the element flick.</param>
    /// <param name="XSpeed">Horizontal velocity, for the anonymous flick.</param>
    /// <param name="YSpeed">Vertical velocity, for the anonymous flick.</param>
    private sealed record TouchRequest(
        [property: JsonPropertyName("element")] string? Element,
        [property: JsonPropertyName("xoffset")] int XOffset,
        [property: JsonPropertyName("yoffset")] int YOffset,

        // THE CLIENT SAYS HOW FAST. JSON Wire gives /touch/flick a speed in
        // PIXELS PER SECOND, and this record did not read it - so a caller that
        // asked for a slow flick and one that asked for a fast one got the same
        // gesture, and the driver's own invented pace won every time.
        [property: JsonPropertyName("speed")] double? Speed,

        // The ANONYMOUS flick form, which carries no element and no offsets at
        // all - only a velocity per axis. The suite sends exactly this:
        // touchScreen.Flick(0, 180). Read as xoffset/yoffset it was two absent
        // properties defaulting to zero, which is a gesture that goes nowhere.
        [property: JsonPropertyName("xspeed")] double? XSpeed,
        [property: JsonPropertyName("yspeed")] double? YSpeed);

    /// <summary>Maps one phase of a multi-request touch gesture.</summary>
    /// <remarks>
    /// <para>
    /// <b>The body carries window-relative coordinates.</b>
    /// <c>TouchDownMoveUp_SingleTap</c> computes them from
    /// <c>element.Location</c> plus half the element's size, and this driver
    /// answers <c>location</c> relative to the window - so treating them as
    /// screen pixels would land the contact that far from the DESKTOP origin,
    /// outside the application under test.
    /// </para>
    /// <para>
    /// <b>Missing coordinates default to zero rather than faulting</b>, matching
    /// the other pointer routes: JSON Wire sends x and y on every one of these,
    /// and inventing a fault for a body the suite never sends would be a rule
    /// with no test behind it.
    /// </para>
    /// </remarks>
    private static void MapContact(
        IEndpointRouteBuilder app, string suffix, SyntheticContactPhase phase)
    {
        app.MapPost($"/session/{{sessionId}}/touch/{suffix}",
            async (
                HttpContext context,
                PointerActionRunner? runner,
                IElementRegistry registry,
                IWindowLocator windows) =>
            {
                DriverSession session = context.GetSession();

                if (runner is null)
                {
                    return Results.Text(NoInjector, statusCode: 501);
                }

                using JsonDocument body = await JsonDocument
                    .ParseAsync(context.Request.Body).ConfigureAwait(false);

                int x = Coordinate(body.RootElement, "x");
                int y = Coordinate(body.RootElement, "y");

                PointerRefusal? failure = runner.Contact(session.WindowHandle, x, y, phase);

                if (failure is null)
                {
                    // A CONTACT IS DISPATCHED INPUT TOO, and these three routes
                    // were the only input path that never said so. click,
                    // longclick, doubleclick, scroll and flick all flag it; a
                    // hand-built gesture spelled down/move/up did not, so a read
                    // straight after one never waited for the application.
                    //
                    // Found by the by-parameter audit lens, which enumerates
                    // what each route touches rather than whether it answers.
                    // The route looked complete: correct coordinates, correct
                    // phase, 200 back.
                    session.InputPending = true;
                }

                return failure is null
                    ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                    : Fault(failure, session, registry, windows);
            }).RequiresSession();
    }

    private static int Coordinate(JsonElement body, string name) =>
        body.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : 0;
}

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Read-only element property routes.
/// </summary>
/// <remarks>
/// Every shape here is a recorded WinAppDriver response. Three are ones a
/// reading of the specification gets wrong: <c>/name</c> answers the tag name
/// rather than the Name property, <c>/size</c> serialises height before width,
/// and <c>/location</c> is relative to the window rather than the screen.
/// </remarks>
public static class ElementPropertyRoutes
{
    /// <summary>Maps the element property routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapElementPropertyRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapRead(app, "text", static (inspector, window, id) => inspector.Text(window, id));
        MapRead(app, "name", static (inspector, window, id) => inspector.TagName(window, id));
        MapRead(app, "enabled", static (inspector, window, id) => inspector.IsEnabled(window, id));
        MapRead(app, "displayed", static (inspector, window, id) => inspector.IsDisplayed(window, id));
        MapRead(app, "selected", static (inspector, window, id) => inspector.IsSelected(window, id));

        // /location and /location_in_view answer identically. Measured — the
        // second does not scroll first, whatever its name suggests.
        MapRead(app, "location", ReadLocation);
        MapRead(app, "location_in_view", ReadLocation);
        MapRead(app, "size", ReadSize);

        // W3C's replacement for location + size, and the route a Selenium 4
        // client uses for BOTH - the specification deleted the other two. This
        // is one of the few places this driver deliberately does more than the
        // reference: WinAppDriver answers 501 here, which is why no Selenium 4
        // test can read an element's geometry through it.
        //
        // Nothing in the compatibility suite requests /rect, so implementing it
        // cannot cost a suite test. Matching a NOT-answer would have been
        // faithfulness to a defect rather than to the protocol.
        MapRead(app, "rect", ReadRect);

        MapAttribute(app);

        return app;
    }

    private const string MissingAttributeNameMessage =
        "Attribute command takes exactly one argument namely the attribute name";

    /// <summary>
    /// Maps <c>GET /element/{id}/attribute/{name}</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MapRead{T}"/> because of the extra route value
    /// and the one case that is a fault rather than a null: an <b>empty</b>
    /// attribute name answers 400 with status 100, while an <b>unknown</b> one
    /// answers 200 with null. Measured, and the distinction is easy to lose —
    /// routing would otherwise send a trailing-slash request to the
    /// unknown-command fallback and answer status 9.
    /// </remarks>
    private static void MapAttribute(IEndpointRouteBuilder app)
    {
        app.MapGet("/session/{sessionId}/element/{elementId}/attribute/{name?}",
            static (
                HttpContext context,
                IElementInspector inspector,
                IElementRegistry registry,
                IWindowLocator windows,
                string elementId,
                string? name) =>
            {
                if (string.IsNullOrEmpty(name))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.InvalidArgument, MissingAttributeNameMessage),
                        statusCode: WebDriverFault.InvalidArgument.HttpStatus);
                }

                DriverSession session = context.GetSession();
                ElementRead<string?> result = inspector.Attribute(
                    session.WindowHandle, elementId, name);

                return result.Outcome == ElementReadOutcome.Read
                    ? Results.Json(JsonWireResponse.ForSession(session.Id, result.Value))
                    : ElementFault.For(result.Outcome, session, elementId, registry, windows);
            })
            .RequiresSession();
    }

    /// <summary>
    /// Maps one property route.
    /// </summary>
    /// <remarks>
    /// The nine routes differ only in which property they read and how the value
    /// is shaped, so the session lookup, the outcome mapping and the envelope are
    /// written once. The implementation being replaced inlined all three per
    /// route, which is how the same failure ended up with three different
    /// messages.
    /// </remarks>
    private static void MapRead<T>(
        IEndpointRouteBuilder app,
        string suffix,
        Func<IElementInspector, nint, string, ElementRead<T>> read)
    {
        app.MapGet($"/session/{{sessionId}}/element/{{elementId}}/{suffix}",
            (HttpContext context,
             IElementInspector inspector,
             IElementRegistry registry,
             IWindowLocator windows,
             ITerminationLog? log,
             string elementId) =>
            {
                DriverSession session = context.GetSession();

                // Pay for typing here, and only when something was typed.
                DrainTypedInput(session, windows, log);

                ElementRead<T> result = read(inspector, session.WindowHandle, elementId);

                return result.Outcome == ElementReadOutcome.Read
                    ? Results.Json(JsonWireResponse.ForSession(session.Id, result.Value))
                    : ElementFault.For(result.Outcome, session, elementId, registry, windows);
            })
            .RequiresSession();
    }

    /// <summary>Waits for typed input to land, if any is in flight.</summary>
    /// <param name="session">The session, whose flag is cleared once drained.</param>
    /// <param name="windows">Used to wait on the application.</param>
    /// <param name="log">Records whether the wait actually ran. Optional.</param>
    /// <remarks>
    /// <para>
    /// <b>Measured 2026-08-11, three candidate primitives, one survivor.</b>
    /// A synchronous <c>WM_NULL</c> is delivered AHEAD of queued input, so it
    /// returns at once and proves only that the thread is responsive.
    /// <c>AttachThreadInput</c> plus <c>GetQueueStatus</c> reported zero pending
    /// keys while 51 were still queued. <c>WaitForInputIdle</c> returned with all
    /// 52 characters present, five times out of five, waiting 46-195 ms.
    /// </para>
    /// <para>
    /// <b>It is not free, which is why it is here and not in <c>/keys</c>.</b>
    /// Typing stays at ~4 ms, a session that never types never waits, and the
    /// cost lands once per typing burst on the read that depends on it. For
    /// comparison, WinAppDriver spends ~2500 ms typing the same 52 characters
    /// and still races.
    /// </para>
    /// </remarks>
    private static void DrainTypedInput(
        DriverSession session, IWindowLocator windows, ITerminationLog? log)
    {
        if (!session.InputPending)
        {
            return;
        }

        // Cleared either way. A wait that fails - no message loop, or a process
        // this driver may not open - must not make every later read retry it.
        session.InputPending = false;

        long started = Stopwatch.GetTimestamp();
        bool waited = windows.WaitForInputProcessed(session.WindowHandle);

        // THE RESULT IS NO LONGER DISCARDED, and that was a real defect by this
        // repository's own rule: a return-value error signal is checked before
        // reading dependent state.
        //
        // It still does not change what the route DOES - refusing the read
        // outright is worse than a racy answer, and the reference does not refuse
        // either. What changes is that a drain which never ran now says so.
        // Measured 2026-08-12: the SendKeysToElement_* family reads text 0.9 ms
        // after the keystroke that should have changed it, and nothing in the
        // transcript could distinguish "waited" from "never ran".
        //
        // Read `waited` and the elapsed time TOGETHER. True at 0 ms is a wait
        // that ran and answered instantly - a different problem from false.
        log?.InputDrained(waited, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static ElementRead<ElementLocation> ReadLocation(
        IElementInspector inspector, nint window, string elementId)
    {
        ElementRead<ElementBounds> bounds = inspector.WindowRelativeBounds(window, elementId);

        return bounds.Outcome == ElementReadOutcome.Read
            ? ElementRead.Success(
                new ElementLocation(bounds.Value.X, bounds.Value.Y))
            : ElementRead.Failed<ElementLocation>(bounds.Outcome);
    }

    /// <summary>Reads one rectangle and reports it whole.</summary>
    /// <remarks>
    /// <b>Window-relative, the same source <c>/location</c> reads.</b> W3C states
    /// coordinates in the top-level browsing context's frame; for a desktop
    /// driver that frame is the window. Reading screen bounds here instead -
    /// which <see cref="IElementInspector"/> will also answer - would have one
    /// driver report two different positions for one element depending on which
    /// route the client asked.
    /// </remarks>
    private static ElementRead<ElementRect> ReadRect(
        IElementInspector inspector, nint window, string elementId)
    {
        ElementRead<ElementBounds> bounds = inspector.WindowRelativeBounds(window, elementId);

        return bounds.Outcome == ElementReadOutcome.Read
            ? ElementRead.Success(new ElementRect(
                bounds.Value.X, bounds.Value.Y, bounds.Value.Width, bounds.Value.Height))
            : ElementRead.Failed<ElementRect>(bounds.Outcome);
    }

    private static ElementRead<ElementSize> ReadSize(
        IElementInspector inspector, nint window, string elementId)
    {
        ElementRead<ElementBounds> bounds = inspector.WindowRelativeBounds(window, elementId);

        return bounds.Outcome == ElementReadOutcome.Read
            ? ElementRead.Success(
                new ElementSize(bounds.Value.Height, bounds.Value.Width))
            : ElementRead.Failed<ElementSize>(bounds.Outcome);
    }
}

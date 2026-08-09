using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
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

        // W3C's replacement for location + size, which WinAppDriver never
        // implemented. Reported the way it reports every unimplemented command:
        // 501 with a plain-text body the client cannot parse, which is what
        // produces its "Unexpected error. " prefix.
        app.MapGet("/session/{sessionId}/element/{elementId}/rect",
            static (HttpContext context) => Results.Text(
                $"Unimplemented Command: {context.Request.Method}: {context.Request.Path}",
                statusCode: StatusCodes.Status501NotImplemented))
            .RequiresSession();

        return app;
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
            (HttpContext context, IElementInspector inspector, IElementRegistry registry, string elementId) =>
            {
                DriverSession session = context.GetSession();
                ElementRead<T> result = read(inspector, session.WindowHandle, elementId);

                return result.Outcome == ElementReadOutcome.Read
                    ? Results.Json(JsonWireResponse.ForSession(session.Id, result.Value))
                    : ElementFault.For(result.Outcome, session.Id, elementId, registry);
            })
            .RequiresSession();
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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>GET /session/{sessionId}/screenshot</c> and
/// <c>GET /session/{sessionId}/element/{elementId}/screenshot</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The suite asserts the SIZE of what comes back</b>, not merely that an
/// image arrived. <c>GetScreenshot</c> compares the decoded image against
/// <c>session.Manage().Window.Size</c> and <c>GetElementScreenshot</c> against
/// the element's own size, then asserts the element capture is strictly smaller
/// than the window's. So the rectangle chosen here is the whole behaviour —
/// capturing the full desktop would satisfy "bytes came back" and fail every
/// one of those assertions.
/// </para>
/// <para>
/// <b>Both routes bring the window forward first.</b> A screen blit copies
/// whatever pixels are on the glass, so an obscured window yields a picture of
/// whatever is covering it. The suite makes this explicit: it maximizes Notepad
/// over Alarms &amp; Clock and then expects the Alarms screenshot to show
/// Alarms, noting that the capture "implicitly brings its window to
/// foreground".
/// </para>
/// </remarks>
public static class ScreenshotRoutes
{
    private const string CaptureFailedMessage = "Failed to capture a screenshot of the window";

    /// <summary>Maps both screenshot routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapScreenshotRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapSessionScreenshot(app);
        MapElementScreenshot(app);

        return app;
    }

    /// <summary>Maps <c>GET /screenshot</c> — the session's own window.</summary>
    /// <remarks>
    /// The WINDOW, not the screen. WinAppDriver captures the whole desktop only
    /// for a Desktop session, where the session's window IS the desktop, so one
    /// rule covers both rather than a special case that could drift.
    /// </remarks>
    private static void MapSessionScreenshot(IEndpointRouteBuilder app)
    {
        app.MapGet("/session/{sessionId}/screenshot",
            static (HttpContext context, IWindowLocator windows, IScreenCapture capture) =>
            {
                DriverSession session = context.GetSession();

                // Asked BEFORE the capture, and this is the load-bearing order.
                // A window that has closed leaves its screen space to whatever
                // is now behind it, so capturing first and checking afterwards
                // would photograph another application and return it to the
                // client as this session's window.
                WindowBounds? bounds = windows.GetBounds(session.WindowHandle);

                if (bounds is null)
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
                        statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
                }

                windows.BringToForeground(session.WindowHandle);

                byte[]? png = capture.CapturePng(
                    bounds.X, bounds.Y, bounds.Width, bounds.Height);

                return Encode(session, png);
            }).RequiresSession();
    }

    /// <summary>Maps <c>GET /element/{elementId}/screenshot</c>.</summary>
    /// <remarks>
    /// Routed through <see cref="IElementInspector.ScreenBounds"/> and
    /// <see cref="ElementFault"/> like every other element read, so a stale
    /// element, an unknown id and a closed window answer here exactly as they
    /// do on <c>/size</c> and <c>/location</c>. Two of the five suite tests for
    /// screenshots are error cases, and they assert the same strings the rest of
    /// the element commands do.
    /// </remarks>
    private static void MapElementScreenshot(IEndpointRouteBuilder app)
    {
        app.MapGet("/session/{sessionId}/element/{elementId}/screenshot",
            static (HttpContext context,
                    IElementInspector inspector,
                    IElementRegistry registry,
                    IWindowLocator windows,
                    IScreenCapture capture,
                    string elementId) =>
            {
                DriverSession session = context.GetSession();

                ElementRead<ElementBounds> bounds =
                    inspector.ScreenBounds(session.WindowHandle, elementId);

                if (bounds.Outcome != ElementReadOutcome.Read)
                {
                    return ElementFault.For(
                        bounds.Outcome, session, elementId, registry, windows);
                }

                windows.BringToForeground(session.WindowHandle);

                byte[]? png = capture.CapturePng(
                    bounds.Value.X, bounds.Value.Y, bounds.Value.Width, bounds.Value.Height);

                return Encode(session, png);
            }).RequiresSession();
    }

    /// <summary>Wraps the bytes as the base64 string JWP carries.</summary>
    /// <remarks>
    /// A failed capture is a fault rather than an empty string. An empty
    /// screenshot decodes to nothing and would surface in a client as a
    /// mysteriously blank image rather than as an error anyone can act on.
    /// </remarks>
    private static IResult Encode(DriverSession session, byte[]? png) =>
        png is null
            ? Results.Json(
                JsonWireResponse.ForFault(WebDriverFault.UnknownError, CaptureFailedMessage),
                statusCode: WebDriverFault.UnknownError.HttpStatus)
            : Results.Json(
                JsonWireResponse.ForSession(session.Id, Convert.ToBase64String(png)));
}

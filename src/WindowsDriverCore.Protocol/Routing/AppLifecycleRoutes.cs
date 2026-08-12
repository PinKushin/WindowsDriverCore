using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>POST /session/{sessionId}/appium/app/close</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth three suite tests</b> — <c>Close_ClassicApp</c>,
/// <c>Close_ModernApp</c> and <c>Close_SystemApp</c> — and the route was absent,
/// answering <c>404 jwp 9</c> three times in the last measured run.
/// </para>
/// <para>
/// <b>The session outlives the application.</b> <c>CloseApplication</c> asserts
/// the session id is still present and that <c>WindowHandles</c> is EMPTY
/// afterwards, so this must not be confused with <c>DELETE /session</c>. It
/// closes the window and leaves the session addressable.
/// </para>
/// <para>
/// <b>Closing twice is a fault, and that is the point of the second half of the
/// test.</b> The suite calls <c>CloseApp</c> again on the already-closed
/// application and requires <c>Currently selected window has been closed</c>, so
/// a second close cannot quietly succeed.
/// </para>
/// <para>
/// <b>The WINDOW is closed, not the process.</b> Terminating by process would
/// take down every window that process owns, including ones a person is using —
/// single-instance applications add a window without adding a process, which is
/// how WinAppDriver ends up killing a user's other windows on Windows 11. That
/// behaviour is a defect worth not reproducing.
/// </para>
/// </remarks>
public static class AppLifecycleRoutes
{
    /// <summary>Maps the application lifecycle routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapAppLifecycleRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/appium/app/close",
            static (HttpContext context, IWindowLocator windows) =>
            {
                DriverSession session = context.GetSession();

                // Asked before closing, so a second close reports the window is
                // already gone rather than answering success for doing nothing.
                if (!windows.Exists(session.WindowHandle) ||
                    !windows.Close(session.WindowHandle))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
                        statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
                }

                // WM_CLOSE is POSTED, so the window is still alive when Close
                // returns. The suite reads WindowHandles IMMEDIATELY afterwards
                // and requires it to be empty, which is a race the client loses
                // unless the wait happens here.
                windows.WaitUntilGone(session.WindowHandle);

                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
            }).RequiresSession();

        return app;
    }
}

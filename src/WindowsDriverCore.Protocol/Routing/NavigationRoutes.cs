using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>POST /session/{sessionId}/back</c> and <c>POST /session/{sessionId}/forward</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth five suite tests</b>, and both routes were absent — measured on the
/// wire, where <c>/back</c> answered <c>404 jwp 9</c> four times and
/// <c>/forward</c> once.
/// </para>
/// <para>
/// <b>Alt+Left is the Windows back gesture, and there is no UIA command for
/// this.</b> Navigation is an application concept rather than an automation
/// one: UIA can invoke a control but has nothing that means "go back". A
/// packaged application handles the system back gesture through
/// <c>BackRequested</c>, which Alt+Left raises, so this synthesizes the gesture
/// a user would make. <c>NavigateBack_ModernApp</c> drives Alarms &amp; Clock
/// from the Add-Alarm view back to the list, which is exactly that path.
/// </para>
/// <para>
/// <b>The window is checked first.</b> <c>NavigateBackError_NoSuchWindow</c>
/// uses an orphaned session and requires
/// <c>Currently selected window has been closed</c> — and without the check
/// the keystroke would be delivered to whatever now holds the foreground, which
/// is somebody else's application.
/// </para>
/// </remarks>
public static class NavigationRoutes
{
    /// <summary>Selenium's key code for the left arrow.</summary>
    private const string LeftArrow = "";

    /// <summary>Selenium's key code for the right arrow.</summary>
    private const string RightArrow = "";

    /// <summary>Selenium's key code for Alt, held rather than typed.</summary>
    private const char Alt = '';

    /// <summary>Maps the navigation routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapNavigationRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapGesture(app, "back", LeftArrow);
        MapGesture(app, "forward", RightArrow);

        return app;
    }

    private static void MapGesture(IEndpointRouteBuilder app, string suffix, string arrow)
    {
        app.MapPost($"/session/{{sessionId}}/{suffix}",
            (HttpContext context, IWindowLocator windows, IKeyboardInput keyboard) =>
            {
                DriverSession session = context.GetSession();

                // BEFORE any input is synthesized. A dead window means the
                // keystroke would land in whatever application now has the
                // foreground - the worst failure this driver has, because the
                // damage is somebody else's.
                if (!windows.Exists(session.WindowHandle))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
                        statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
                }

                // Focus first, for the same reason: synthesized keys go to the
                // foreground window, not to a handle.
                windows.BringToForeground(session.WindowHandle);

                HeldModifiers held = new();
                held.Hold(Alt);

                if (!keyboard.Type(arrow, held))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.UnknownError,
                            $"The system refused the {suffix} gesture"),
                        statusCode: WebDriverFault.UnknownError.HttpStatus);
                }

                // Released explicitly. A modifier left down outlives the request
                // and turns every later keystroke in the run into Alt+key -
                // measured before on this driver's modifier handling.
                keyboard.ReleaseHeld(held);

                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
            }).RequiresSession();
    }
}

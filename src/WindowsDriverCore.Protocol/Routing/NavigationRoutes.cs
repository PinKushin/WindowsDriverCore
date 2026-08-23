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

                // LET THE APPLICATION FINISH WHAT WE ALREADY SENT IT.
                //
                // MEASURED on the guest by diffing a passing run of
                // NavigateBack_ModernApp against a failing one - it fails in 8
                // of 10 runs. In a passing run the second /back raises the
                // discard dialog in 40 ms and the suite dismisses it; in a
                // failing run no dialog appears and the APPLICATION EXITS, so
                // every later find answers no such window.
                //
                // The only difference upstream is speed. Time from the AddAlarm
                // click to finding EditAlarmHeader:
                //
                //   passing run   122 ms
                //   failing run    66 ms
                //
                // We reached the edit page twice as fast and sent Alt+Left
                // before it had set up its navigation state, so the gesture did
                // not mean "go back within the app". WinAppDriver never hits
                // this because it is slow enough to wait by accident.
                //
                // The dependency here is input-after-input, not the
                // read-after-write that put WaitForInputProcessed in
                // ElementPropertyRoutes and nowhere else. Same primitive - the
                // only one of three candidates that worked, 52 of 52 typed
                // characters - applied to the other ordering.
                //
                // Skipped when nothing is outstanding, so a session that has
                // dispatched no input pays nothing, and the wait returns as soon
                // as the application is idle rather than after a fixed interval.
                if (session.InputPending)
                {
                    session.InputPending = false;
                    windows.WaitForInputProcessed(session.WindowHandle);
                }

                // Focus first, for the same reason: synthesized keys go to the
                // foreground window, not to a handle.
                windows.BringToForeground(session.WindowHandle);

                // ALT IS PRESSED BY THE KEY STRING, not by HeldModifiers.
                //
                // BuildBatch treats a carried modifier as ALREADY PHYSICALLY
                // DOWN and deliberately does not press it again - HeldModifiers
                // is a carry-over mechanism for state a previous call left held,
                // not an instruction to press. Passing a fresh HeldModifiers
                // therefore sent the arrow UNMODIFIED, and the ReleaseHeld that
                // followed sent a lone Alt key-up with no key-down before it,
                // desynchronising modifier state for every later request in the
                // run. Measured on the guest: the three navigation tests failed
                // with a plain arrow, and SendKeysToElement_ModifierAlt then
                // failed with a modifier stuck down.
                //
                // A modifier character INSIDE the string toggles: the first
                // occurrence presses it, the second releases it. So Alt, arrow,
                // Alt is press, tap, release - self-contained, with nothing left
                // held when the request returns.
                if (!keyboard.Type($"{Alt}{arrow}{Alt}"))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.UnknownError,
                            $"The system refused the {suffix} gesture"),
                        statusCode: WebDriverFault.UnknownError.HttpStatus);
                }

                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
            }).RequiresSession();
    }
}

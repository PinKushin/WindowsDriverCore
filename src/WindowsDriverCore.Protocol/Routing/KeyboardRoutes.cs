using WindowsDriverCore.Diagnostics;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>POST /session/{id}/keys</c> — typing at the session's window.
/// </summary>
/// <remarks>
/// <para>
/// 12 tests on the compatibility suite, measured 2026-08-10.
/// </para>
/// <para>
/// <b>Keystrokes go where focus is, and this route does not move focus.</b>
/// That is the protocol's behaviour, not a shortcut: the session-level keys
/// command types at whatever the application has focused, which is why the suite
/// pairs it with a click or an element command first.
/// </para>
/// </remarks>
public static class KeyboardRoutes
{

    /// <summary>Maps the keyboard routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapKeyboardRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/keys",
            async (HttpContext context, IKeyboardInput keyboard, IWindowLocator windows,
                   ITerminationLog log) =>
        {
            DriverSession session = context.GetSession();

            if (!windows.Exists(session.WindowHandle))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
                    statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
            }

            // Bring the SESSION'S window forward before typing. Keystrokes go
            // wherever focus is, and a suite commonly has several applications
            // open at once — measured 2026-08-10, with Calculator, Notepad and
            // Alarms all alive during one compatibility run. Without this the
            // keys land in whichever window happens to be in front, which is
            // usually not the one the session addresses.
            //
            // The keyboard itself is not the problem and was proven so: typing
            // "Wx9!" through this driver into Notepad returned "Wx9!" exactly.
            // ATTEMPTED, NOT REQUIRED — and refusing here was a DEADLOCK.
            //
            // Windows will not let an ordinary process take the foreground from
            // the shell's own surfaces. SendKeys_ModifierWindowsKey opens the
            // Action Center with Win+A and dismisses it by typing Escape; while
            // that pane is up this driver could not foreground anything, so it
            // refused to type, so the pane was never dismissed, so every later
            // /keys in that class refused too. Measured on the guest: with the
            // pane in front, EVERY /keys answered HTTP 500 - including the one
            // that would have closed it.
            //
            // The keys are sent regardless. They go where focus is, which for the
            // caller trying to dismiss a pane is exactly the pane. That is also
            // what the reference does: WinAppDriver does not gate typing on the
            // foreground at all, which is why its ModifierWindowsKey closes the
            // pane and ours left it open.
            //
            // Zeroing SPI_SETFOREGROUNDLOCKTIMEOUT was tried for this and does
            // NOT help - measured twice, once through the whole suite and once
            // through a probe with the patched binary in place.
            // RECORDED, still not acted upon. Refusing is the deadlock above, so
            // the keys go out either way - but whether the raise worked is the
            // difference between "typed into the application" and "typed into
            // whatever was in front", and those are indistinguishable from the
            // response alone. Five SendKeys tests failed with an EMPTY target in
            // 20-27 ms, which is what the second case looks like, and nothing on
            // the wire could tell them apart.
            long raiseStarted = Stopwatch.GetTimestamp();
            bool raised = windows.BringToForeground(session.WindowHandle);

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body).ConfigureAwait(false);

            string? keys = ReadValue(body.RootElement);

            if (keys is null)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.InvalidArgument, "Missing Command Parameter: value"),
                    statusCode: WebDriverFault.InvalidArgument.HttpStatus);
            }

            // CARRIED, not released. A modifier left open by this call stays
            // physically down for the next one, which is what the protocol
            // describes and what SendKeys_ModifierExplicitRelease asserts.
            // DELETE /session lifts whatever survives.
            // Described only when the raise FAILED. On the success path it costs
            // nothing and says nothing - the window that holds the foreground is
            // the one we just raised.
            log.KeysDispatched(
                raised,
                raised ? string.Empty : windows.DescribeForeground(),
                Stopwatch.GetElapsedTime(raiseStarted).TotalMilliseconds);

            if (!keyboard.Type(keys, session.Modifiers))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownError, "The keystrokes were not accepted"),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            }

            // Record that input is in flight rather than waiting for it here.
            // Waiting would cost ~100 ms on EVERY /keys, including the three-call
            // clear sequence the suite runs before each test. The read that
            // actually depends on the keystrokes pays instead - once.
            session.InputPending = true;

            return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
        }).RequiresSession();

        return app;
    }

    /// <summary>Joins the "value" array into one sequence.</summary>
    /// <remarks>
    /// The protocol sends an ARRAY of strings and they concatenate — a modifier
    /// can be its own array entry, so joining is what keeps
    /// <c>["", "a", ""]</c> meaning hold-press-release rather than
    /// three unrelated sequences. An empty array is a valid request that types
    /// nothing, which the suite sends deliberately.
    /// </remarks>
    private static string? ReadValue(JsonElement body)
    {
        if (!body.TryGetProperty("value", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        StringBuilder keys = new();

        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                keys.Append(entry.GetString());
            }
        }

        return keys.ToString();
    }
}

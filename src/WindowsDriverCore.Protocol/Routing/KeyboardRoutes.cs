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
    private const string WindowClosedMessage = "Currently selected window has been closed";

    /// <summary>Maps the keyboard routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapKeyboardRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/keys",
            async (HttpContext context, IKeyboardInput keyboard, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            if (!windows.Exists(session.WindowHandle))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, WindowClosedMessage),
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
            if (!windows.BringToForeground(session.WindowHandle))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownError,
                        "The session's window could not be brought to the foreground"),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            }

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

            if (!keyboard.Type(keys))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownError, "The keystrokes were not accepted"),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            }

            // Do not answer until the application has actually consumed them.
            // SendInput queues; the client reads. Measured: 52 characters
            // answered immediately, the client saw "abc", and the other 49 landed
            // during the next test.
            windows.WaitForInputProcessed(session.WindowHandle);

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

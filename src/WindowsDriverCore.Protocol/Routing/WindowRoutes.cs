using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>A window's size, as the protocol reports it.</summary>
/// <param name="Height">Height in pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <remarks>
/// Height before width, which is the order the recorded response uses. Harmless
/// to a parser and free to match.
/// </remarks>
public sealed record WindowSize(
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("width")] int Width);

/// <summary>A window's position, as the protocol reports it.</summary>
/// <param name="X">Left edge in screen pixels.</param>
/// <param name="Y">Top edge in screen pixels.</param>
public sealed record WindowPosition(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

/// <summary>
/// Window inspection routes.
/// </summary>
/// <remarks>
/// <para>
/// Measured 2026-08-10 against the compatibility suite in the Windows 10 guest:
/// <c>GET /title</c> blocked 14 tests, <c>GET /window_handle</c> 10 and
/// <c>GET /window/current/size</c> 11.
/// </para>
/// <para>
/// Every shape here comes from <c>Recordings/winappdriver-responses.json</c>,
/// including the two that would be easy to get subtly wrong: a window handle is
/// an <b>eight digit uppercase hex string with an 0x prefix</b>, and size
/// serialises height before width.
/// </para>
/// </remarks>
public static class WindowRoutes
{

    /// <summary>Measured from the real server, not invented.</summary>
    private const string SwitchFailedMessage =
        "A request to switch to a window could not be satisfied because the window could not be found.";

    /// <summary>Maps the window routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapWindowRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/session/{sessionId}/window_handle", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            return windows.Exists(session.WindowHandle)
                ? Results.Json(JsonWireResponse.ForSession(session.Id, FormatHandle(session.WindowHandle)))
                : WindowClosed();
        }).RequiresSession();

        app.MapGet("/session/{sessionId}/window_handles", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            // A DESKTOP SESSION OWNS NO APPLICATION WINDOWS, so it answers
            // empty rather than reporting the desktop window it is rooted at.
            // GetWindowHandles_Desktop asserts exactly zero, and returning the
            // root would be reporting a window the session cannot close, move
            // or switch away from.
            //
            // Otherwise every window this session has opened that is still
            // alive. Liveness is asked here rather than trusted from the list,
            // because a window the session opened may have been closed since -
            // and Launch_ModernApp requires the count to RISE by one when the
            // application is relaunched, which a single handle cannot express.
            IReadOnlyList<string> handles = session.IsDesktop
                ? []
                : [.. session.OwnedWindows
                    .Where(windows.Exists)
                    .Select(FormatHandle)];

            return Results.Json(JsonWireResponse.ForSession(session.Id, handles));
        }).RequiresSession();

        app.MapGet("/session/{sessionId}/title", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            return windows.Exists(session.WindowHandle)
                ? Results.Json(JsonWireResponse.ForSession(session.Id, windows.GetTitle(session.WindowHandle)))
                : WindowClosed();
        }).RequiresSession();

        app.MapGet("/session/{sessionId}/window/current/size", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();
            WindowBounds? bounds = windows.GetBounds(session.WindowHandle);

            return bounds is null
                ? WindowClosed()
                : Results.Json(JsonWireResponse.ForSession(
                    session.Id, new WindowSize(bounds.Height, bounds.Width)));
        }).RequiresSession();

        app.MapGet("/session/{sessionId}/window/current/position", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();
            WindowBounds? bounds = windows.GetBounds(session.WindowHandle);

            return bounds is null
                ? WindowClosed()
                : Results.Json(JsonWireResponse.ForSession(
                    session.Id, new WindowPosition(bounds.X, bounds.Y)));
        }).RequiresSession();

        app.MapPost("/session/{sessionId}/window/current/size",
            async (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();
            WindowBounds? bounds = windows.GetBounds(session.WindowHandle);

            if (bounds is null)
            {
                return WindowClosed();
            }

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body).ConfigureAwait(false);

            if (!Number(body.RootElement, "width", out int width) ||
                !Number(body.RootElement, "height", out int height))
            {
                return BadParameter("width, height");
            }

            // Position is kept: this route sets SIZE, and moving the window as a
            // side effect would be a surprise the caller never asked for.
            return windows.SetBounds(session.WindowHandle, new WindowBounds(bounds.X, bounds.Y, width, height))
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : WindowClosed();
        }).RequiresSession();

        app.MapPost("/session/{sessionId}/window/current/position",
            async (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();
            WindowBounds? bounds = windows.GetBounds(session.WindowHandle);

            if (bounds is null)
            {
                return WindowClosed();
            }

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body).ConfigureAwait(false);

            if (!Number(body.RootElement, "x", out int x) ||
                !Number(body.RootElement, "y", out int y))
            {
                return BadParameter("x, y");
            }

            return windows.SetBounds(session.WindowHandle, new WindowBounds(x, y, bounds.Width, bounds.Height))
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : WindowClosed();
        }).RequiresSession();

        app.MapPost("/session/{sessionId}/window/current/maximize", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            return windows.Maximize(session.WindowHandle)
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : WindowClosed();
        }).RequiresSession();

        app.MapDelete("/session/{sessionId}/window", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            // Closes the WINDOW and leaves the session alive — the suite closes
            // a window and then keeps using its session id, so this must not be
            // confused with DELETE /session.
            if (!windows.Close(session.WindowHandle))
            {
                return WindowClosed();
            }

            // WM_CLOSE is POSTED, so the window is still alive when Close
            // returns. The suite closes a window and immediately uses an element
            // from it, expecting "Currently selected window has been closed" —
            // answering before the window has gone makes that a race the client
            // loses, and it answers an element error instead.
            windows.WaitUntilGone(session.WindowHandle);

            return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
        }).RequiresSession();

        app.MapPost("/session/{sessionId}/window", async (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body).ConfigureAwait(false);

            string? name = body.RootElement.TryGetProperty("name", out JsonElement value)
                ? value.GetString()
                : null;

            // EMPTY counts as missing, not as an unparseable handle.
            // SwitchWindowsError_EmptyValue sends "" and asserts
            // "Missing Command Parameter: name"; treating it as a bad handle
            // answered "the window could not be found" instead.
            if (string.IsNullOrEmpty(name))
            {
                return BadParameter("name");
            }

            // Hexadecimal, like every other window handle on this wire. Parsing
            // it as decimal would silently address a different window.
            //
            // A MALFORMED handle is reported with the .NET parse message rather
            // than as a missing window: SwitchWindowsError_InvalidValue sends
            // "-1" and asserts, character for character,
            // "String cannot contain a minus sign if the base is not 10." That
            // is Convert.ToInt32's own wording surfacing through WinAppDriver,
            // so it is reproduced by attempting the same conversion rather than
            // by copying the sentence and hoping every other malformed input
            // happens to match.
            string digits = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? name[2..]
                : name;

            nint handle;
            try
            {
                handle = (nint)Convert.ToInt64(digits, 16);
            }
            catch (Exception failure) when (
                failure is ArgumentException or FormatException or OverflowException)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(WebDriverFault.UnknownError, failure.Message),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            }

            if (!windows.Exists(handle))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, SwitchFailedMessage),
                    statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
            }

            // A CHILD window is refused by name. SwitchWindowsError_NonTopLevelWindowHandle
            // hands over the CoreWindow's handle, which exists and belongs to
            // this very application - so every other check passes and only this
            // one separates it from a legitimate switch.
            if (!windows.IsTopLevel(handle))
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownError, $"{name} is not a top level window handle"),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            }

            // ANOTHER APPLICATION'S window is refused, and the packaged case is
            // why this asks twice. A packaged app's frame is owned by
            // ApplicationFrameHost while its content belongs to the app, so
            // comparing only the owning process would reject the session's OWN
            // window and break every legitimate switch.
            if (windows.GetOwningProcessId(handle) != session.ProcessId &&
                windows.GetHostedProcessId(handle) != session.ProcessId)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownError,
                        "Window handle does not belong to the same process/application"),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            }

            session.WindowHandle = handle;

            return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
        }).RequiresSession();

        return app;
    }

    /// <summary>Formats a handle the way the recorded responses do.</summary>
    /// <remarks>
    /// <c>0x00551120</c> — lowercase prefix, uppercase digits, padded to eight.
    /// A client that round-trips this string back as a window id would not match
    /// a differently-formatted one, so the format is part of the contract rather
    /// than a presentation choice.
    /// </remarks>
    private static string FormatHandle(nint handle) =>
        "0x" + ((long)handle).ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Reads an integer that a client may have sent as a double.</summary>
    /// <remarks>
    /// A WebDriver client serialises window sizes from doubles, so 500 arrives
    /// as 500.0. Demanding an integer token would reject a perfectly ordinary
    /// request.
    /// </remarks>
    private static bool Number(JsonElement body, string name, out int value)
    {
        value = 0;

        if (!body.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = (int)Math.Round(element.GetDouble());
        return true;
    }

    private static IResult BadParameter(string names) =>
        Results.Json(
            JsonWireResponse.ForFault(
                WebDriverFault.InvalidArgument, $"Missing Command Parameter: {names}"),
            statusCode: WebDriverFault.InvalidArgument.HttpStatus);

    private static IResult WindowClosed() =>
        Results.Json(
            JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
            statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
}

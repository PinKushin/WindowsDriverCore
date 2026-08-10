using System.Collections.Generic;
using System.Globalization;
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
    private const string WindowClosedMessage = "Currently selected window has been closed";

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

            // One handle, because a session addresses one window. The plural
            // route exists because clients call it; it does not imply this
            // driver tracks a window list it does not have.
            IReadOnlyList<string> handles = windows.Exists(session.WindowHandle)
                ? [FormatHandle(session.WindowHandle)]
                : [];

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

    private static IResult WindowClosed() =>
        Results.Json(
            JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, WindowClosedMessage),
            statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
}

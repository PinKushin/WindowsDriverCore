using WindowsDriverCore.Diagnostics;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
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

/// <summary>A window's whole rectangle, which is how W3C reports it.</summary>
/// <param name="X">Left edge in screen pixels.</param>
/// <param name="Y">Top edge in screen pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <remarks>
/// <b>W3C replaced size and position with one rectangle</b>, and there is no
/// JSON Wire route a client can fall back to - so a Selenium 4 client without
/// this cannot read or set window geometry at all.
///
/// Every field is nullable because W3C makes each optional on the way IN: a
/// null means "leave this one alone", not "set it to zero". The same record
/// serves the response, where all four are always present.
/// </remarks>
public sealed record WindowRect(int? X, int? Y, int? Width, int? Height);

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

    /// <summary>The handle segment that means "the window this session drives".</summary>
    private const string CurrentWindow = "current";

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

        // W3C RENAMED BOTH OF THESE, and serving only the JSON Wire spelling
        // means a Selenium 4 client cannot ask which window it is on OR what
        // windows exist - two of the most ordinary questions a client has.
        //
        //   JSON Wire            W3C
        //   /window_handle       /window
        //   /window_handles      /window/handles
        //
        // Registered as aliases onto the SAME handlers rather than reimplemented,
        // because two copies of "which windows are alive" is exactly how
        // WinAppDriver's own XPath singular and plural drifted apart into issue
        // #1079. The response envelope is translated by the dialect filter, so
        // only the path differs.
        //
        // The POST and DELETE on /session/{id}/window already existed - switch
        // and close - so only the GET was missing there.
        app.MapGet("/session/{sessionId}/window_handle", CurrentHandle).RequiresSession();
        app.MapGet("/session/{sessionId}/window", CurrentHandle).RequiresSession();

        app.MapGet("/session/{sessionId}/window_handles", AllHandles).RequiresSession();
        app.MapGet("/session/{sessionId}/window/handles", AllHandles).RequiresSession();

        app.MapGet("/session/{sessionId}/title",
            (HttpContext context, IWindowLocator windows, IElementInspector inspector) =>
        {
            DriverSession session = context.GetSession();

            if (!windows.Exists(session.WindowHandle))
            {
                return WindowClosed();
            }

            // THE DESKTOP HAS NO WIN32 CAPTION, so GetWindowText answers an
            // empty string for it and GetTitle_Desktop and CreateSession_Desktop
            // both fail asserting a title that starts with "Desktop".
            //
            // A desktop session's handle is GetDesktopWindow(). UIA calls that
            // element "Desktop 1" - the desktop is a UIA concept before it is a
            // Win32 one, so the name is read where the concept lives. Every
            // other session keeps the cheaper Win32 read.
            if (session.IsDesktop)
            {
                ElementRead<string> name = inspector.WindowName(session.WindowHandle);

                return name.Outcome == ElementReadOutcome.Read
                    ? Results.Json(JsonWireResponse.ForSession(session.Id, name.Value))
                    : WindowClosed();
            }

            return Results.Json(
                JsonWireResponse.ForSession(session.Id, windows.GetTitle(session.WindowHandle)));
        }).RequiresSession();

        // THE HANDLE SEGMENT IS A PARAMETER, not the literal word "current".
        //
        // WinAppDriver documents /window/:windowHandle/size, /position and
        // /maximize, and this driver served only /window/current/... - so a
        // client addressing a window by its actual handle, which is what the
        // path is FOR, got "Command not recognized". The compatibility suite
        // sends "current" (Selenium 3.8's Manage().Window does), which is why
        // 290 green tests never showed it.
        //
        // Measured 2026-08-29 by probing all 59 endpoints in WinAppDriver's own
        // SupportedAPIs.md against a running server: eleven answered
        // unknown-command, and this family was six of them.
        MapGeometry(app, "size", ReadSize, WriteSize);
        MapGeometry(app, "position", ReadPosition, WritePosition);

        // W3C DROPPED THE HANDLE FROM THE PATH. JSON Wire addresses a window as
        // /window/{handle}/maximize, with "current" as the alias meaning the
        // session's own; W3C is simply /window/maximize. Same handler, all
        // three spellings.
        app.MapPost("/session/{sessionId}/window/{windowHandle}/maximize", MaximizeWindow)
            .RequiresSession();
        app.MapPost("/session/{sessionId}/window/maximize", MaximizeWindow).RequiresSession();

        // THE OTHER HALF OF MAXIMIZE, which W3C defines and this driver did not
        // serve — so a client could enlarge a window and never put it back.
        //
        // Both spellings for symmetry with maximize, even though W3C only
        // defines the handle-less one: a JSON Wire client that has been
        // addressing windows by handle everywhere else should not have to
        // change shape for this one command.
        app.MapPost("/session/{sessionId}/window/{windowHandle}/minimize", MinimizeWindow)
            .RequiresSession();
        app.MapPost("/session/{sessionId}/window/minimize", MinimizeWindow).RequiresSession();

        // W3C REPLACED SIZE AND POSITION WITH ONE RECTANGLE. A client that
        // wants to know where a window is, or to put it somewhere, has no
        // JSON Wire route to fall back to - so without these a Selenium 4
        // client cannot read or set window geometry at all.
        app.MapGet("/session/{sessionId}/window/rect", (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();
            WindowBounds? bounds = windows.GetBounds(session.WindowHandle);

            return bounds is null
                ? WindowClosed()
                : Results.Json(JsonWireResponse.ForSession(
                    session.Id,
                    new WindowRect(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
        }).RequiresSession();

        app.MapPost("/session/{sessionId}/window/rect",
            async (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            WindowRect? wanted = await context.Request
                .ReadFromJsonAsync<WindowRect>(context.RequestAborted)
                .ConfigureAwait(false);

            WindowBounds? current = windows.GetBounds(session.WindowHandle);
            if (current is null)
            {
                return WindowClosed();
            }

            // EVERY FIELD IS OPTIONAL IN W3C, and a null means "leave it alone"
            // rather than "move it to zero". A client nudging only the position
            // must not have its window resized to nothing.
            WindowBounds target = new(
                wanted?.X ?? current.X,
                wanted?.Y ?? current.Y,
                wanted?.Width ?? current.Width,
                wanted?.Height ?? current.Height);

            return windows.SetBounds(session.WindowHandle, target)
                ? Results.Json(JsonWireResponse.ForSession(
                    session.Id,
                    new WindowRect(target.X, target.Y, target.Width, target.Height)))
                : WindowClosed();
        }).RequiresSession();

        app.MapDelete("/session/{sessionId}/window",
            (HttpContext context, IWindowLocator windows, ITerminationLog log) =>
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
            long started = Stopwatch.GetTimestamp();
            bool gone = windows.WaitUntilGone(session.WindowHandle);

            // RECORDED, and still not acted upon. The wait is bounded because an
            // application showing "save changes?" may never close and the close
            // request was still delivered, so answering success remains right.
            // But a window that OUTLIVES the wait is currently invisible on the
            // wire, and it is the leading suspect for the *Error_NoSuchWindow
            // tests that flap: a command issued straight afterwards finds the
            // window alive and answers "no such element". Logging it first,
            // because three theories for that flapping have already been
            // measured and refuted, and a fourth guess is worth less than one
            // line of evidence from a real run.
            log.WindowClosed(
                session.WindowHandle,
                gone,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
        }).RequiresSession();

        app.MapPost("/session/{sessionId}/window", async (HttpContext context, IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body).ConfigureAwait(false);

            // W3C SPELLS THE SAME COMMAND DIFFERENTLY. JSON Wire switches windows
            // by "name", W3C by "handle", and reading only the first meant a
            // Selenium 4 client got "Missing Command Parameter: name" for a
            // perfectly well-formed request. Selenium 4 support is a stated goal
            // of this driver, so a request shape it cannot read is a gap in the
            // goal rather than a nicety.
            //
            // Same class as the /timeouts and /rect request shapes already
            // fixed, and as the "text" spelling on element/value - which is the
            // point: the RESPONSE dialect was translated in one place and the
            // REQUEST shapes were left to be found one at a time.
            string? name = body.RootElement.TryGetProperty("name", out JsonElement value)
                ? value.GetString()
                : null;

            // The W3C spelling, read only when the JSON Wire one is ABSENT.
            // A client sending an empty "name" has still named the parameter,
            // and SwitchWindowsError_EmptyValue asserts it is reported as
            // missing rather than falling through to a different key.
            if (!body.RootElement.TryGetProperty("name", out _) &&
                body.RootElement.TryGetProperty("handle", out JsonElement w3cHandle))
            {
                name = w3cHandle.GetString();
            }

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

    /// <summary>Maximizes the session window.</summary>
    /// <param name="context">The request, carrying the session.</param>
    /// <param name="windows">Performs the maximize.</param>
    /// <returns>Void, or the closed-window fault.</returns>
    /// <remarks>
    /// Served at both <c>/window/current/maximize</c> (JSON Wire) and
    /// <c>/window/maximize</c> (W3C).
    /// </remarks>
    private static IResult MaximizeWindow(HttpContext context, IWindowLocator windows)
    {
        DriverSession session = context.GetSession();

        (nint handle, IResult? refusal) = AddressedWindow(context, session, windows);

        if (refusal is not null)
        {
            return refusal;
        }

        return windows.Maximize(handle)
            ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
            : WindowClosed();
    }

    private static IResult MinimizeWindow(HttpContext context, IWindowLocator windows)
    {
        DriverSession session = context.GetSession();

        (nint handle, IResult? refusal) = AddressedWindow(context, session, windows);

        if (refusal is not null)
        {
            return refusal;
        }

        return windows.Minimize(handle)
            ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
            : WindowClosed();
    }

    /// <summary>Registers one geometry property under all three path spellings.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="property">The path segment, <c>size</c> or <c>position</c>.</param>
    /// <param name="read">Turns bounds into the response body.</param>
    /// <param name="write">Turns a request body plus current bounds into new bounds.</param>
    /// <remarks>
    /// Three spellings, one handler each way:
    /// <c>/window/{handle}/{property}</c> as WinAppDriver documents it,
    /// and <c>/window/{property}</c>, which its own list also carries. The
    /// literal <c>current</c> is not a fourth registration — it arrives through
    /// the parameter and is recognised by <see cref="AddressedWindow"/>.
    /// </remarks>
    private static void MapGeometry(
        IEndpointRouteBuilder app,
        string property,
        Func<WindowBounds, object> read,
        Func<JsonElement, WindowBounds, WindowBounds?> write)
    {
        app.MapGet($"/session/{{sessionId}}/window/{{windowHandle}}/{property}", Read).RequiresSession();
        app.MapGet($"/session/{{sessionId}}/window/{property}", Read).RequiresSession();

        app.MapPost($"/session/{{sessionId}}/window/{{windowHandle}}/{property}", Write).RequiresSession();
        app.MapPost($"/session/{{sessionId}}/window/{property}", Write).RequiresSession();

        IResult Read(HttpContext context, IWindowLocator windows)
        {
            DriverSession session = context.GetSession();

            (nint handle, IResult? refusal) = AddressedWindow(context, session, windows);
            if (refusal is not null)
            {
                return refusal;
            }

            WindowBounds? bounds = windows.GetBounds(handle);

            return bounds is null
                ? WindowClosed()
                : Results.Json(JsonWireResponse.ForSession(session.Id, read(bounds)));
        }

        async Task<IResult> Write(HttpContext context, IWindowLocator windows)
        {
            DriverSession session = context.GetSession();

            (nint handle, IResult? refusal) = AddressedWindow(context, session, windows);
            if (refusal is not null)
            {
                return refusal;
            }

            WindowBounds? bounds = windows.GetBounds(handle);

            if (bounds is null)
            {
                return WindowClosed();
            }

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body).ConfigureAwait(false);

            WindowBounds? wanted = write(body.RootElement, bounds);

            if (wanted is null)
            {
                return BadParameter(property == "size" ? "width, height" : "x, y");
            }

            return windows.SetBounds(handle, wanted)
                ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                : WindowClosed();
        }
    }

    /// <summary>Height before width, which is how the real server serialises it.</summary>
    private static object ReadSize(WindowBounds bounds) =>
        new WindowSize(bounds.Height, bounds.Width);

    private static object ReadPosition(WindowBounds bounds) =>
        new WindowPosition(bounds.X, bounds.Y);

    /// <summary>New bounds from a size request, or null if the body is malformed.</summary>
    /// <remarks>
    /// Position is kept: this route sets SIZE, and moving the window as a side
    /// effect would be a surprise the caller never asked for.
    /// </remarks>
    private static WindowBounds? WriteSize(JsonElement body, WindowBounds bounds) =>
        Number(body, "width", out int width) && Number(body, "height", out int height)
            ? new WindowBounds(bounds.X, bounds.Y, width, height)
            : null;

    /// <summary>New bounds from a position request, or null if the body is malformed.</summary>
    private static WindowBounds? WritePosition(JsonElement body, WindowBounds bounds) =>
        Number(body, "x", out int x) && Number(body, "y", out int y)
            ? new WindowBounds(x, y, bounds.Width, bounds.Height)
            : null;

    /// <summary>Which window a <c>{windowHandle}</c> segment addresses.</summary>
    /// <param name="context">The request, carrying the route values.</param>
    /// <param name="session">The session, which owns the default window.</param>
    /// <param name="windows">Answers who owns a handle.</param>
    /// <returns>The handle, or the fault to report instead.</returns>
    /// <remarks>
    /// <para>
    /// Three cases, and only the first was previously reachable:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Absent, or the literal <c>current</c> — the session's own window. This is
    /// what Selenium 3.8 sends and what the compatibility suite scores.
    /// </description></item>
    /// <item><description>
    /// A hex handle the session's process owns — that window. The point of the
    /// path parameter, and previously an unknown command.
    /// </description></item>
    /// <item><description>
    /// Anything else — refused as no such window. <b>Not silently redirected to
    /// the session's window</b>, which is the tempting shortcut: it would move a
    /// window the client did not name and report success for it.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static (nint Handle, IResult? Refusal) AddressedWindow(
        HttpContext context, DriverSession session, IWindowLocator windows)
    {
        object? segment = context.Request.RouteValues.GetValueOrDefault("windowHandle");

        if (segment is not string named ||
            named.Length == 0 ||
            string.Equals(named, CurrentWindow, StringComparison.OrdinalIgnoreCase))
        {
            return (session.WindowHandle, null);
        }

        string digits = named.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? named[2..]
            : named;

        if (!nint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nint handle) ||
            !windows.Exists(handle) ||
            windows.GetOwningProcessId(handle) != session.ProcessId)
        {
            return (0, Results.Json(
                JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, SwitchFailedMessage),
                statusCode: WebDriverFault.NoSuchWindow.HttpStatus));
        }

        return (handle, null);
    }

    /// <summary>The handle of the window this session is driving.</summary>
    /// <param name="context">The request, carrying the session.</param>
    /// <param name="windows">Answers whether the window is still there.</param>
    /// <returns>The handle, or the closed-window fault.</returns>
    /// <remarks>
    /// Served at BOTH <c>/window_handle</c> (JSON Wire) and <c>/window</c>
    /// (W3C). One handler rather than two, because two copies of the same
    /// question are how WinAppDriver's own XPath singular and plural drifted
    /// apart into issue #1079.
    /// </remarks>
    private static IResult CurrentHandle(HttpContext context, IWindowLocator windows)
    {
        DriverSession session = context.GetSession();

        return windows.Exists(session.WindowHandle)
            ? Results.Json(JsonWireResponse.ForSession(session.Id, FormatHandle(session.WindowHandle)))
            : WindowClosed();
    }

    /// <summary>Every window this session owns that is still alive.</summary>
    /// <param name="context">The request, carrying the session.</param>
    /// <param name="windows">Answers whether each window is still there.</param>
    /// <returns>The handles.</returns>
    /// <remarks>
    /// <para>
    /// Served at BOTH <c>/window_handles</c> (JSON Wire) and
    /// <c>/window/handles</c> (W3C).
    /// </para>
    /// <para>
    /// <b>A DESKTOP SESSION OWNS NO APPLICATION WINDOWS</b>, so it answers empty
    /// rather than reporting the desktop window it is rooted at.
    /// <c>GetWindowHandles_Desktop</c> asserts exactly zero, and returning the
    /// root would be reporting a window the session cannot close, move or switch
    /// away from.
    /// </para>
    /// <para>
    /// Liveness is asked here rather than trusted from the list, because a
    /// window the session opened may have been closed since - and
    /// <c>Launch_ModernApp</c> requires the count to RISE by one when the
    /// application is relaunched, which a single handle cannot express.
    /// </para>
    /// </remarks>
    private static IResult AllHandles(HttpContext context, IWindowLocator windows)
    {
        DriverSession session = context.GetSession();

        IReadOnlyList<string> handles = session.IsDesktop
            ? []
            : [.. session.OwnedWindows
                .Where(windows.Exists)
                .Select(FormatHandle)];

        return Results.Json(JsonWireResponse.ForSession(session.Id, handles));
    }

    private static IResult WindowClosed() =>
        Results.Json(
            JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
            statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
}

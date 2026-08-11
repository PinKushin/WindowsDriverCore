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
/// The JSON Wire Protocol mouse family: <c>/moveto</c>, <c>/click</c>,
/// <c>/buttondown</c>, <c>/buttonup</c> and <c>/doubleclick</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are not decoration, and their absence is not confined to their own
/// tests.</b> The compatibility suite deletes the alarms it creates with
/// <c>DeletePreviouslyCreatedAlarmEntry</c>, which is an XPath find followed by
/// <c>session.Mouse.ContextClick</c> — a <c>/moveto</c> and then a
/// <c>/click</c> with <c>button 2</c> — all inside a bare
/// <c>catch { break; }</c>. Answering "command not recognized" made every
/// deletion a silent no-op, so alarms accumulated run after run until Alarms
/// &amp; Clock stopped allowing new ones and nine unrelated tests began failing.
/// A missing command with a blast radius nowhere near itself.
/// </para>
/// <para>
/// <b>There is no stored cursor position.</b> <c>/moveto</c> really moves the
/// pointer and <c>/click</c> acts wherever the pointer now is, because that is
/// what the protocol describes and what a hand does. Tracking a coordinate in
/// the session would be a second source of truth that the user's own mouse could
/// silently invalidate.
/// </para>
/// <para>
/// Every status here is measured against WinAppDriver 1.2.1, recorded in
/// <c>Recordings/winappdriver-mouse-xpath.json</c>:
/// </para>
/// <list type="bullet">
///   <item><description><c>moveto</c> with an element, with or without offsets — 200.</description></item>
///   <item><description><c>moveto</c> with offsets and no element, or a null element — 200.</description></item>
///   <item><description><c>moveto</c> with an unknown element — 404.</description></item>
///   <item><description><c>moveto</c> with an empty body — 400.</description></item>
///   <item><description><c>click</c> with no button, 0, or 2 — 200; with 9 — 400.</description></item>
/// </list>
/// </remarks>
public static class MouseRoutes
{
    /// <summary>The highest button number the protocol defines.</summary>
    private const int HighestButton = 2;

    private const string NoPointer =
        "This driver was built without pointer input, so mouse commands cannot be served.";

    /// <summary>Maps the mouse routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapMouseRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/moveto",
            async (
                HttpContext context,
                IElementInspector inspector,
                IPointerInput? pointer) =>
            {
                MoveToRequest? request = await context.Request
                    .ReadFromJsonAsync<MoveToRequest>(context.RequestAborted)
                    .ConfigureAwait(false);

                DriverSession session = context.GetSession();

                if (pointer is null)
                {
                    return Fault(WebDriverFault.UnknownError, NoPointer);
                }

                // An empty body is 400. Measured: {} is rejected while
                // {"xoffset":10,"yoffset":10} is accepted, so the discriminator
                // is "did the caller say where", not "did it name an element".
                bool hasElement = !string.IsNullOrEmpty(request?.Element);
                bool hasOffset = request?.XOffset is not null && request?.YOffset is not null;

                if (!hasElement && !hasOffset)
                {
                    return Fault(
                        WebDriverFault.InvalidArgument,
                        "moveto needs an element, or an xoffset and a yoffset.");
                }

                int x;
                int y;

                if (hasElement)
                {
                    ElementRead<ElementBounds> bounds =
                        inspector.ScreenBounds(session.WindowHandle, request!.Element!);

                    if (bounds.Outcome != ElementReadOutcome.Read)
                    {
                        return Fault(
                            WebDriverFault.NoSuchElement,
                            "An element could not be located on the page using the given search parameters.");
                    }

                    // With offsets, they are measured from the element's
                    // top-left; without them, the protocol says the centre.
                    x = hasOffset
                        ? bounds.Value.X + request.XOffset!.Value
                        : bounds.Value.X + (bounds.Value.Width / 2);
                    y = hasOffset
                        ? bounds.Value.Y + request.YOffset!.Value
                        : bounds.Value.Y + (bounds.Value.Height / 2);
                }
                else
                {
                    // No element: the offsets are relative to where the pointer
                    // already is, not to the screen origin.
                    if (!pointer.TryGetPosition(out int currentX, out int currentY))
                    {
                        return Fault(WebDriverFault.UnknownError, "The cursor position could not be read.");
                    }

                    x = currentX + request!.XOffset!.Value;
                    y = currentY + request.YOffset!.Value;
                }

                return pointer.MoveTo(x, y)
                    ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                    : Fault(WebDriverFault.UnknownError, "The system rejected the pointer input.");
            })
            .RequiresSession();

        MapButtonAction(app, "click", static (pointer, button) => pointer.Click(button));
        MapButtonAction(app, "buttondown", static (pointer, button) => pointer.Press(button));
        MapButtonAction(app, "buttonup", static (pointer, button) => pointer.Release(button));
        MapButtonAction(app, "doubleclick", static (pointer, button) => pointer.DoubleClick(button));

        return app;
    }

    private static void MapButtonAction(
        IEndpointRouteBuilder app,
        string suffix,
        Func<IPointerInput, PointerButton, bool> act)
    {
        app.MapPost($"/session/{{sessionId}}/{suffix}",
            async (HttpContext context, IPointerInput? pointer) =>
            {
                ButtonRequest? request = await context.Request
                    .ReadFromJsonAsync<ButtonRequest>(context.RequestAborted)
                    .ConfigureAwait(false);

                DriverSession session = context.GetSession();

                if (pointer is null)
                {
                    return Fault(WebDriverFault.UnknownError, NoPointer);
                }

                // Absent means left. Measured: {} and {"button":0} both answer
                // 200, and {"button":9} answers 400.
                int button = request?.Button ?? (int)PointerButton.Left;

                if (button < 0 || button > HighestButton)
                {
                    return Fault(
                        WebDriverFault.InvalidArgument,
                        $"Button {button} is not one of 0 (left), 1 (middle) or 2 (right).");
                }

                if (!act(pointer, (PointerButton)button))
                {
                    return Fault(WebDriverFault.UnknownError, "The system rejected the pointer input.");
                }

                // Dispatched, not yet consumed. The next read that depends on it
                // waits; this call does not, so a burst of clicks pays once.
                session.InputPending = true;

                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
            })
            .RequiresSession();
    }

    private static IResult Fault(WebDriverFault fault, string message) =>
        Results.Json(JsonWireResponse.ForFault(fault, message), statusCode: fault.HttpStatus);

    /// <summary>The body of <c>POST /session/{id}/moveto</c>.</summary>
    /// <param name="Element">The element to move to, if any.</param>
    /// <param name="XOffset">Offset from the element's left edge, or from the current position.</param>
    /// <param name="YOffset">Offset from the element's top edge, or from the current position.</param>
    private sealed record MoveToRequest(string? Element, int? XOffset, int? YOffset);

    /// <summary>The body of the button routes.</summary>
    /// <param name="Button">0 left, 1 middle, 2 right. Absent means left.</param>
    private sealed record ButtonRequest(int? Button);
}

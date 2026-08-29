using System.Collections.Generic;
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

/// <summary>The body of <c>POST /element/{id}/value</c>.</summary>
/// <param name="Value">
/// <param name="Text">The W3C spelling: one string rather than an array.</param>
/// The text, as an array of strings. Selenium 3 and the Appium .NET client both
/// send the characters split across an array rather than a single string, so it
/// is joined rather than indexed.
/// </param>
public sealed record SetValueRequest(
    [property: JsonPropertyName("value")] IReadOnlyList<string>? Value,

    // W3C SPELLS THE SAME COMMAND DIFFERENTLY, and reading only the JSON Wire
    // form meant a Selenium 4 client typed nothing at all - the request parsed,
    // the route answered 200, and no text arrived. Selenium 4 support is a
    // stated goal of this driver, so a request shape it cannot read is a gap in
    // the goal rather than a nicety.
    //
    // JSON Wire: {"value": ["h","i"]}   an array of single characters
    // W3C:       {"text": "hi"}          one string
    [property: JsonPropertyName("text")] string? Text)
{
    /// <summary>The text to type, whichever dialect the client used.</summary>
    /// <remarks>
    /// <b>The array wins when both are present.</b> Selenium 3 sends only
    /// <c>value</c>, Selenium 4 only <c>text</c>, and a client sending both has
    /// said the same thing twice - but the array is the one this driver has
    /// always honoured, so preferring it cannot change any behaviour that
    /// already works.
    /// </remarks>
    public string? Typed =>
        Value is { Count: > 0 } ? string.Concat(Value) : Text;
}

/// <summary>
/// Element routes that change something: click, clear, value.
/// </summary>
public static class ElementActionRoutes
{
    private const string NotInteractableMessage =
        "An element command could not be completed because the element is not " +
        "pointer- or keyboard interactable.";

    /// <summary>Maps the element action routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapElementActionRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapAction(app, "click", static (interactor, window, id) => interactor.Click(window, id));
        MapAction(app, "clear", static (interactor, window, id) => interactor.Clear(window, id));

        app.MapPost("/session/{sessionId}/element/{elementId}/value",
            async (
                HttpContext context,
                IElementInteractor interactor,
                IElementRegistry registry,
                IWindowLocator windows,
                string elementId) =>
            {
                SetValueRequest? request = await context.Request
                    .ReadFromJsonAsync<SetValueRequest>(context.RequestAborted)
                    .ConfigureAwait(false);

                DriverSession session = context.GetSession();

                // TYPING, with no pattern gate, and the recording is why.
                //
                // This route used to refuse an element with no ValuePattern,
                // "the recorded contract answers 400 ElementNotInteractable".
                // That claim was never in the recording. What the recording
                // actually holds is
                // error.element.sendKeysDisabled.ClearMemoryButton: POST /value
                // with {"value":["x"]} against a DISABLED Calculator button,
                // answered 200 status 0. WinAppDriver types at whatever element
                // it is given.
                //
                // The gate was not free. SendKeys_ModifierWindowsKey dismisses
                // the Action Center it opened by sending Escape to the pane, the
                // pane has no ValuePattern, and the refusal left it on screen
                // holding the foreground - which failed every later test in that
                // class with "could not be brought to the foreground".
                ElementAction action = interactor.SendKeys(
                    session.WindowHandle,
                    elementId,
                    request?.Typed ?? string.Empty);

                if (action.Outcome == ElementActionOutcome.Performed)
                {
                    // THE DRAIN THIS ROUTE STOPPED NEEDING AND THEN NEEDED AGAIN.
                    //
                    // A ValuePattern write is finished when the call returns.
                    // Typing is not: SendInput queues keystrokes and returns, so
                    // the .Text read that follows can win the race and see the
                    // old contents. MapAction has marked its actions pending
                    // since the ladder was written; this route was a
                    // ValuePattern write and did not need to, and switching it to
                    // the keyboard quietly made it need to.
                    //
                    // Measured 2026-08-11: four SendKeysToElement tests passed
                    // and four different ones in the same family failed between
                    // two runs of identical code. The COUNT was unchanged, which
                    // is what makes a race easy to read as no change at all —
                    // only the names moved.
                    session.InputPending = true;
                }

                return Respond(action, session, elementId, registry, windows);
            })
            .RequiresSession();

        return app;
    }

    private static void MapAction(
        IEndpointRouteBuilder app,
        string suffix,
        Func<IElementInteractor, nint, string, ElementAction> act)
    {
        app.MapPost($"/session/{{sessionId}}/element/{{elementId}}/{suffix}",
            (HttpContext context,
             IElementInteractor interactor,
             IElementRegistry registry,
             IWindowLocator windows,
             string elementId) =>
            {
                DriverSession session = context.GetSession();

                ElementAction performed = act(interactor, session.WindowHandle, elementId);

                if (performed.Outcome == ElementActionOutcome.Performed)
                {
                    // Every rung of the ladder makes the application do work -
                    // a pattern invocation, a focus change, a real mouse click -
                    // and none of it is finished when the call returns. The next
                    // read that depends on it waits; this one does not.
                    session.InputPending = true;
                }

                return Respond(performed, session, elementId, registry, windows);
            })
            .RequiresSession();
    }

    /// <summary>
    /// The response for an action, measured.
    /// </summary>
    /// <remarks>
    /// Success carries a session id and a status and <b>no value at all</b> —
    /// not <c>"value": null</c>. Recorded as
    /// <c>{"sessionId":"…","status":0}</c>.
    /// </remarks>
    private static IResult Respond(
        ElementAction action,
        DriverSession session,
        string elementId,
        IElementRegistry registry,
        IWindowLocator windows) =>
        action.Outcome switch
        {
            ElementActionOutcome.Performed =>
                Results.Json(JsonWireResponse.ForSessionVoid(session.Id)),

            ElementActionOutcome.NotInteractable => Results.Json(
                JsonWireResponse.ForFault(
                    WebDriverFault.ElementNotInteractable, NotInteractableMessage),
                statusCode: WebDriverFault.ElementNotInteractable.HttpStatus),

            // Stale versus never-issued, and the closed-window case, are the same
            // question the read routes answer, so they use the same code.
            ElementActionOutcome.NoSuchWindow =>
                ElementFault.For(ElementReadOutcome.NoSuchWindow, session, elementId, registry, windows),

            _ => ElementFault.For(ElementReadOutcome.NotFound, session, elementId, registry, windows),
        };
}

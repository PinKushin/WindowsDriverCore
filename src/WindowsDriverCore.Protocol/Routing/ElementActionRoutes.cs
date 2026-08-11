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
/// The text, as an array of strings. Selenium 3 and the Appium .NET client both
/// send the characters split across an array rather than a single string, so it
/// is joined rather than indexed.
/// </param>
public sealed record SetValueRequest(
    [property: JsonPropertyName("value")] IReadOnlyList<string>? Value);

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

                // BOTH HALVES, WHICH IS WHY THIS IS NOT SetValue AND NOT
                // SendKeys.
                //
                // The recorded contract answers 400 ElementNotInteractable for an
                // element that cannot hold a value, which plain typing would turn
                // into a 200 by typing at a button. But SetValue cannot express
                // what the suite actually sends: SendKeys(Control+A) then
                // SendKeys(Delete) arrived as a ValuePattern write of the literal
                // key CODES, so the box ended up holding U+E009 and U+E017 —
                // invisible, non-empty, and the initializer's
                // Assert.AreEqual(string.Empty, Text) failed with
                // "Expected:<>. Actual:<>." Eleven tests died there.
                //
                // TypeValue gates on the pattern and acts with the keyboard.
                ElementAction action = interactor.TypeValue(
                    session.WindowHandle,
                    elementId,
                    string.Concat(request?.Value ?? []));

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

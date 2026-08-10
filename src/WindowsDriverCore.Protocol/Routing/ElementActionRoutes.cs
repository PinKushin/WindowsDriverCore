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

                // SetValue, NOT SendKeys, and that is the recorded contract
                // rather than a preference. Measured behaviour: this route
                // answers 400 ElementNotInteractable for an element that cannot
                // hold a value, which typing would turn into a 200. Switching it
                // to send-keys was tried and reverted for exactly that.
                //
                // The suite's SendKeysToElement tests want typing semantics —
                // Control+A then Delete to clear, Alt+Enter to move focus — so
                // those need a path that does not contradict this recording.
                ElementAction action = interactor.SetValue(
                    session.WindowHandle,
                    elementId,
                    string.Concat(request?.Value ?? []));

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

                return Respond(
                    act(interactor, session.WindowHandle, elementId),
                    session, elementId, registry, windows);
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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>POST /session/{sessionId}/element/active</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth four tests on the compatibility suite</b>, and the route was
/// entirely absent — measured on the wire, where it answered
/// <c>404 jwp 9</c> (unknown command) five times in one run.
/// </para>
/// <para>
/// <b>POST, not GET.</b> Selenium 3's <c>SwitchTo().ActiveElement()</c> issues
/// a POST with an empty body, which is what the JSON Wire Protocol specifies
/// even though the command only reads. Mapping GET would leave the suite
/// hitting the unknown-command fallback exactly as it did before.
/// </para>
/// <para>
/// <b>Focus elsewhere is a SUCCESS carrying an empty id, not a fault.</b>
/// <c>GetActiveElement_Empty</c> opens the Windows start menu to take focus
/// away and then requires a non-null element whose id is
/// <c>string.Empty</c> — so answering "no such element" would be the wrong
/// shape of response entirely, not merely the wrong text.
/// </para>
/// </remarks>
public static class ActiveElementRoutes
{
    /// <summary>Maps the active element route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapActiveElementRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // W3C CHANGED THE VERB. JSON Wire asks for the focused element with a
        // POST; W3C uses a GET, and a Selenium 4 client therefore got the
        // unknown-command fallback for a command this driver fully implements.
        //
        // The same delegate under both verbs rather than a second copy - two
        // implementations of "which element has focus" is how WinAppDriver's own
        // XPath singular and plural drifted apart into issue #1079.
        app.MapPost("/session/{sessionId}/element/active",
            static (HttpContext context,
                    IElementInspector inspector,
                    IElementRegistry registry,
                    IWindowLocator windows) =>
            {
                DriverSession session = context.GetSession();

                ElementRead<string> focused = inspector.FocusedElementId(session.WindowHandle);

                if (focused.Outcome != ElementReadOutcome.Read)
                {
                    // There is no element id to attribute this to - the failure
                    // is the window, not an element the client named - so the
                    // id passed to the fault is empty rather than invented.
                    return ElementFault.For(
                        focused.Outcome, session, string.Empty, registry, windows);
                }

                string elementId = focused.Value ?? string.Empty;

                // Recorded only when there IS one. Recording an empty id would
                // put a value in the issued-id set that no client can ever use,
                // and would make a later empty answer look like a stale element.
                if (elementId.Length > 0)
                {
                    registry.Record(session.Id, elementId);
                }

                return Results.Json(
                    JsonWireResponse.ForSession(session.Id, new ElementReference(elementId)));
            }).RequiresSession();

        app.MapGet("/session/{sessionId}/element/active",
            static (HttpContext context,
                    IElementInspector inspector,
                    IElementRegistry registry,
                    IWindowLocator windows) =>
            {
                DriverSession session = context.GetSession();

                ElementRead<string> focused = inspector.FocusedElementId(session.WindowHandle);

                if (focused.Outcome != ElementReadOutcome.Read)
                {
                    // There is no element id to attribute this to - the failure
                    // is the window, not an element the client named - so the
                    // id passed to the fault is empty rather than invented.
                    return ElementFault.For(
                        focused.Outcome, session, string.Empty, registry, windows);
                }

                string elementId = focused.Value ?? string.Empty;

                // Recorded only when there IS one. Recording an empty id would
                // put a value in the issued-id set that no client can ever use,
                // and would make a later empty answer look like a stale element.
                if (elementId.Length > 0)
                {
                    registry.Record(session.Id, elementId);
                }

                return Results.Json(
                    JsonWireResponse.ForSession(session.Id, new ElementReference(elementId)));
            }).RequiresSession();

        return app;
    }
}

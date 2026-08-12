using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>GET /session/{sessionId}/element/{elementId}/equals/{otherId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Comparing the id strings IS comparing the elements.</b> An element id here
/// is the UIA RuntimeId — <c>UiaElementFinder</c> mints it from the runtime id
/// and nothing else — and a RuntimeId is unique per element and stable for as
/// long as that element lives. Two references to one element therefore carry
/// identical strings, and two different elements never do. That makes the
/// string comparison exact rather than an approximation, and it avoids a UIA
/// round trip that would only re-derive what the id already encodes.
/// </para>
/// <para>
/// <b>Both ids are probed first, and the fault names whichever one failed.</b>
/// The comparison itself cannot fail, but either operand can be dead — and the
/// suite draws that distinction: <c>CompareElementsError_NoSuchElement</c> has a
/// bad first element, <c>CompareElementsError_StaleElementParameter</c> a bad
/// second. Since stale-versus-unknown is decided by which id this server
/// issued, asking about the wrong operand would answer for the wrong element.
/// </para>
/// </remarks>
public static class ElementEqualsRoutes
{
    /// <summary>Maps the element comparison route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapElementEqualsRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/session/{sessionId}/element/{elementId}/equals/{otherId}",
            static (HttpContext context,
                    IElementInspector inspector,
                    IElementRegistry registry,
                    IWindowLocator windows,
                    string elementId,
                    string otherId) =>
            {
                DriverSession session = context.GetSession();

                // A cheap read that exists on every element, used only to ask
                // whether the id still resolves. TagName is the least expensive
                // thing the inspector can be asked for, and the VALUE is
                // discarded - the outcome is the whole question.
                IResult? faulted =
                    Probe(inspector, registry, windows, session, elementId) ??
                    Probe(inspector, registry, windows, session, otherId);

                return faulted ?? Results.Json(
                    JsonWireResponse.ForSession(
                        session.Id,
                        string.Equals(elementId, otherId, StringComparison.Ordinal)));
            }).RequiresSession();

        return app;
    }

    /// <summary>The fault for an operand that no longer resolves, or null.</summary>
    private static IResult? Probe(
        IElementInspector inspector,
        IElementRegistry registry,
        IWindowLocator windows,
        DriverSession session,
        string elementId)
    {
        ElementRead<string> read = inspector.TagName(session.WindowHandle, elementId);

        return read.Outcome == ElementReadOutcome.Read
            ? null
            : ElementFault.For(read.Outcome, session, elementId, registry, windows);
    }
}

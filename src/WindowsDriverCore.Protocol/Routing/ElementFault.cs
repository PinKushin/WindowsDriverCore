using Microsoft.AspNetCore.Http;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Turns a failed element read into the fault WinAppDriver would have sent.
/// </summary>
/// <remarks>
/// One place, because every element route needs the same answer and the
/// stale-versus-unknown rule is the kind of thing that drifts when it is
/// written out per route.
/// </remarks>
internal static class ElementFault
{
    private const string NoSuchElementMessage =
        "An element could not be located on the page using the given search parameters.";

    private const string StaleElementMessage =
        "An element command failed because the referenced element is no longer attached to the DOM.";

    private const string WindowClosedMessage = "Currently selected window has been closed";

    /// <summary>The fault for an outcome that is not a successful read.</summary>
    /// <param name="outcome">What the inspector reported.</param>
    /// <param name="sessionId">The session, which scopes the issued-id record.</param>
    /// <param name="elementId">The id the client sent.</param>
    /// <param name="registry">The record of ids this server has handed out.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="ElementReadOutcome.Read"/>, which
    /// is not a fault and means the caller checked the wrong thing.
    /// </exception>
    internal static IResult For(
        ElementReadOutcome outcome, string sessionId, string elementId, IElementRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(elementId);
        ArgumentNullException.ThrowIfNull(registry);

        switch (outcome)
        {
            case ElementReadOutcome.NoSuchWindow:
                return Fault(WebDriverFault.NoSuchWindow, WindowClosedMessage);

            case ElementReadOutcome.NotFound:
                // Stale on the first touch, unknown on every touch after it.
                // TryConsume answers and forgets in one step, which is what makes
                // the second touch answer differently from the first — measured
                // against WinAppDriver, and asserted by the compatibility suite's
                // GetStaleElement tests.
                return registry.TryConsume(sessionId, elementId)
                    ? Fault(WebDriverFault.StaleElementReference, StaleElementMessage)
                    : Fault(WebDriverFault.NoSuchElement, NoSuchElementMessage);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(outcome), outcome, "A successful read is not a fault.");
        }
    }

    private static IResult Fault(WebDriverFault fault, string message) =>
        Results.Json(JsonWireResponse.ForFault(fault, message), statusCode: fault.HttpStatus);
}

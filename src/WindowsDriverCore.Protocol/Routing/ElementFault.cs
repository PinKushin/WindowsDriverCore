using Microsoft.AspNetCore.Http;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
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

    /// <summary>What a command against a closed window is told.</summary>
    /// <remarks>
    /// Public and declared once, because it was declared four times — in
    /// <see cref="ElementRoutes"/>, <see cref="KeyboardRoutes"/>,
    /// <see cref="WindowRoutes"/> and here. The compatibility suite compares it
    /// character for character, so four copies is four chances to answer the same
    /// condition with three slightly different messages.
    /// </remarks>
    public const string WindowClosedMessage = "Currently selected window has been closed";

    /// <summary>The fault for an outcome that is not a successful read.</summary>
    /// <param name="outcome">What the inspector reported.</param>
    /// <param name="session">The session, which scopes the issued-id record and names the window.</param>
    /// <param name="elementId">The id the client sent.</param>
    /// <param name="registry">The record of ids this server has handed out.</param>
    /// <param name="windows">Answers whether the session's window is still there.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="ElementReadOutcome.Read"/>, which
    /// is not a fault and means the caller checked the wrong thing.
    /// </exception>
    internal static IResult For(
        ElementReadOutcome outcome,
        DriverSession session,
        string elementId,
        IElementRegistry registry,
        IWindowLocator windows)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(elementId);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(windows);

        string sessionId = session.Id;

        // A dead WINDOW outranks a missing element, and asking is the only way
        // to tell them apart: to a lookup, "the element is gone" and "everything
        // is gone" are the same observation. Answering "no such element" when
        // the window has closed sends a client looking for a better locator for
        // something no locator can reach — and a retry loop keeps searching a
        // window that no longer exists until it times out.
        //
        // 32 tests on the compatibility suite assert this. The find route
        // already got it; the element commands did not.
        if (outcome != ElementReadOutcome.Read && !windows.Exists(session.WindowHandle))
        {
            return Fault(WebDriverFault.NoSuchWindow, WindowClosedMessage);
        }

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

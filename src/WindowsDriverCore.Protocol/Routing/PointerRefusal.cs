using WindowsDriverCore.Automation;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Why a pointer action was not performed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A string was not enough, and the compatibility suite measured that.</b>
/// The pointer routes used to return a message and nothing else, so a failed
/// element read arrived at the route as the sentence
/// <c>"The element could not be located: NotFound"</c> — an enum formatted into
/// prose. <c>TouchLongTapError_StaleElement</c> compares the message character
/// for character against <c>stale element reference</c> and failed on it.
/// </para>
/// <para>
/// <b>The decision belongs to <c>ElementFault</c>, which already had it.</b>
/// Stale versus never-issued depends on what this server handed out, which the
/// runner has no business knowing. So the runner reports the outcome it observed
/// and the id it observed it for, and the route asks the one place that decides.
/// Carrying the answer instead of the observation is what produced two rules
/// where there should have been one.
/// </para>
/// </remarks>
/// <param name="Message">
/// What went wrong, for the refusals that are genuinely this layer's to explain —
/// a system that cannot inject the requested kind, or an injection the system
/// rejected.
/// </param>
/// <param name="ElementOutcome">
/// Set when the refusal was a failed element read, and null otherwise. Never
/// <see cref="ElementReadOutcome.Read"/>: a successful read is not a refusal.
/// </param>
/// <param name="ElementId">The id that failed to read, set with the outcome.</param>
public sealed record PointerRefusal(
    string Message,
    ElementReadOutcome? ElementOutcome,
    string? ElementId)
{
    /// <summary>A refusal this layer explains itself.</summary>
    /// <param name="message">What went wrong.</param>
    /// <returns>The refusal.</returns>
    public static PointerRefusal Reason(string message) => new(message, null, null);

    /// <summary>A refusal caused by an element that could not be read.</summary>
    /// <param name="outcome">What the inspector reported.</param>
    /// <param name="elementId">The id the caller sent.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// The message is a fallback for a caller that does not translate the
    /// outcome. Every route in this server does, so it should not reach a client.
    /// </remarks>
    public static PointerRefusal Element(ElementReadOutcome outcome, string elementId) =>
        new($"The element could not be located: {outcome}", outcome, elementId);
}

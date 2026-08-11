namespace WindowsDriverCore.Automation;

/// <summary>How an action on an element ended.</summary>
public enum ElementActionOutcome
{
    /// <summary>Something was actually done to the element.</summary>
    Performed,

    /// <summary>
    /// No element in the tree has that id. Stale versus never-issued is decided
    /// in the protocol layer.
    /// </summary>
    NotFound,

    /// <summary>The search root window no longer exists.</summary>
    NoSuchWindow,

    /// <summary>
    /// Nothing on the element, or on its nearest ancestors, could carry out the
    /// action.
    /// </summary>
    /// <remarks>
    /// <b>This must be reported, never swallowed.</b> The implementation being
    /// replaced ended its ladder with <c>SetFocus()</c> and returned success — an
    /// operation indistinguishable from a working one. A caller cannot tell a
    /// click that did nothing from a click that worked unless the driver says so.
    /// </remarks>
    NotInteractable,
}

/// <summary>
/// The result of an action, and which rung of the ladder carried it out.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Path">
/// How it was done — <c>Invoke</c>, <c>SelectionItem</c>, <c>ancestor:1/Toggle</c>.
/// Empty when nothing was done.
/// </param>
/// <remarks>
/// <see cref="Path"/> is not decoration. Pattern activation and a real mouse
/// click differ in observable ways — occlusion, focus, hover — so "the click
/// succeeded" is an incomplete answer without which mechanism ran. It is also
/// what makes the ladder testable: a test can assert that a MAUI composite fell
/// through to the mouse rather than merely that it was clicked.
/// </remarks>
public readonly record struct ElementAction(ElementActionOutcome Outcome, string Path)
{
    /// <summary>An action that was carried out.</summary>
    /// <param name="path">Which rung did it.</param>
    /// <returns>The result.</returns>
    public static ElementAction Performed(string path) =>
        new(ElementActionOutcome.Performed, path);

    /// <summary>An action that was not carried out.</summary>
    /// <param name="outcome">Why.</param>
    /// <returns>The result.</returns>
    public static ElementAction Failed(ElementActionOutcome outcome) =>
        new(outcome, string.Empty);
}

/// <summary>
/// Acts on an element: clicking it, clearing it, setting its value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Patterns before pointers, and the reason is reliability as much as
/// reach.</b> A pattern names the element; a coordinate names a place. Between
/// computing a coordinate and delivering input, the window can be dragged, the
/// application can re-lay out, and another window can be raised over the point —
/// none of which the input stream knows about, so the click lands somewhere else
/// and reports success. Naming the element is immune to all of it.
/// </para>
/// <para>
/// See <c>docs/CLICK-SEMANTICS.md</c> for the ladder, the field evidence behind
/// each rung, and the divergences from a real mouse click that pattern
/// activation carries.
/// </para>
/// </remarks>
public interface IElementInteractor
{
    /// <summary>Clicks an element.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>What happened, and which rung did it.</returns>
    ElementAction Click(nint window, string elementId);

    /// <summary>Empties an element's value.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// Measured: WinAppDriver answers 200 for <c>/clear</c> on an element with no
    /// ValuePattern — a Calculator button. So an element with nothing to clear is
    /// a success, not an error, and this is one of the few places where doing
    /// nothing and reporting success is the contract rather than a defect.
    /// </remarks>
    ElementAction Clear(nint window, string elementId);

    /// <summary>Sets an element's value.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <param name="value">The text to set.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// Through ValuePattern, not synthesized keystrokes. That is a divergence
    /// worth knowing about — no key events reach the application, so anything
    /// driven by <c>KeyDown</c> rather than by the value changing will not fire.
    /// It is the deterministic path, and an element that cannot take a value this
    /// way reports <see cref="ElementActionOutcome.NotInteractable"/> rather than
    /// silently doing nothing. See <c>docs/LIMITATIONS.md</c>.
    /// </remarks>
    ElementAction SetValue(nint window, string elementId, string value);

    /// <summary>Focuses an element and types at it.</summary>
    /// <param name="window">The search root.</param>
    /// <param name="elementId">The element to type into.</param>
    /// <param name="keys">A WebDriver key sequence, modifiers included.</param>
    /// <returns>What happened, and by which path.</returns>
    /// <remarks>
    /// <b>Not the same operation as <see cref="SetValue"/>, and the difference is
    /// observable.</b> SetValue replaces the contents through ValuePattern;
    /// typing sends keystrokes, so Control+A then Delete clears the field and
    /// Alt+Enter moves focus off it. The compatibility suite asserts exactly
    /// those, and no ValuePattern call can produce them.
    /// </remarks>
    ElementAction SendKeys(nint window, string elementId, string keys);
}

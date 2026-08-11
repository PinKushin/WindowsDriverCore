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

    /// <summary>
    /// What <c>POST /element/{id}/value</c> does: refuse anything that cannot
    /// hold a value, and otherwise TYPE.
    /// </summary>
    /// <param name="window">The search root.</param>
    /// <param name="elementId">The element to type into.</param>
    /// <param name="keys">A WebDriver key sequence, modifiers included.</param>
    /// <returns>What happened, and by which path.</returns>
    /// <remarks>
    /// <para>
    /// <b>Both halves are measured, and neither method alone satisfies them.</b>
    /// The recorded WinAppDriver response for an element with no value is
    /// <c>400 ElementNotInteractable</c>, which <see cref="SendKeys"/> would turn
    /// into a 200 by happily typing at a button. And the suite clears its edit box
    /// with <c>Control+A</c> then <c>Delete</c>, which <see cref="SetValue"/>
    /// cannot express — it wrote the key CODES into the box as literal
    /// private-use characters instead.
    /// </para>
    /// <para>
    /// That produced the strangest failure of 2026-08-11:
    /// <c>Assert.AreEqual failed. Expected:&lt;&gt;. Actual:&lt;&gt;.</c> Both
    /// sides print as nothing, because U+E009 and U+E017 are invisible — the box
    /// was not empty, it held two unprintable characters. Eleven tests died in
    /// that initializer before running a line of their own.
    /// </para>
    /// <para>
    /// So the pattern decides whether to act and the keyboard performs it. Kept
    /// as its own operation rather than a flag on either, because a caller asking
    /// for a ValuePattern write and a caller asking to type are asking different
    /// questions and both still have somewhere to go.
    /// </para>
    /// </remarks>
    ElementAction TypeValue(nint window, string elementId, string keys);
}

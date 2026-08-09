namespace WindowsDriverCore.Automation;

/// <summary>How an element property read ended.</summary>
public enum ElementReadOutcome
{
    /// <summary>The property was read.</summary>
    Read,

    /// <summary>
    /// No element in the tree has that id. Whether that is a stale element or an
    /// id this server never issued is decided in the protocol layer, which is
    /// the only place that knows what was issued.
    /// </summary>
    NotFound,

    /// <summary>The search root window no longer exists.</summary>
    NoSuchWindow,
}

/// <summary>An element's rectangle, in whichever space the caller asked for.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct ElementBounds(int X, int Y, int Width, int Height);

/// <summary>The result of reading one property.</summary>
/// <typeparam name="T">The property's type.</typeparam>
/// <param name="Value">The value, meaningful only when <paramref name="Outcome"/> is Read.</param>
/// <param name="Outcome">What happened.</param>
public readonly record struct ElementRead<T>(T? Value, ElementReadOutcome Outcome);

/// <summary>Builds <see cref="ElementRead{T}"/> values.</summary>
/// <remarks>
/// Separate from the struct because CA1000 forbids static members on a generic
/// type, and it reads better anyway: <c>ElementRead.Success("Five")</c> infers
/// its type argument where <c>ElementRead&lt;string&gt;.Success</c> repeats it.
/// </remarks>
public static class ElementRead
{
    /// <summary>A successful read.</summary>
    /// <typeparam name="T">The property's type.</typeparam>
    /// <param name="value">What was read.</param>
    /// <returns>The result.</returns>
    public static ElementRead<T> Success<T>(T value) => new(value, ElementReadOutcome.Read);

    /// <summary>A read that could not happen.</summary>
    /// <typeparam name="T">The property's type.</typeparam>
    /// <param name="outcome">Why.</param>
    /// <returns>The result.</returns>
    public static ElementRead<T> Failed<T>(ElementReadOutcome outcome) => new(default, outcome);
}

/// <summary>
/// Reads properties of an element named by its id.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes an id rather than an element, and resolves it itself. That
/// keeps the COM object's lifetime inside one call — the caller cannot forget to
/// release it — and it keeps <c>IUIAutomationElement</c> out of the protocol
/// layer entirely, so route handlers stay testable without faking COM.
/// </para>
/// <para>
/// It costs a tree walk per call, because UIA rejects RuntimeId in a property
/// condition and there is no other way to find an element by id. That is one
/// walk per HTTP request, which is what a client asking for seven properties
/// pays anyway. Reading several properties per walk is a real optimisation and
/// belongs behind a measurement, not ahead of one.
/// </para>
/// </remarks>
public interface IElementInspector
{
    /// <summary>The tag name, as <c>ControlType.Button</c>.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>The tag name.</returns>
    /// <remarks>
    /// Prefixed, unlike the <c>tag name</c> locator which takes <c>Button</c>.
    /// Measured, and asserted by WinAppDriver's own <c>GetElementTagName</c>.
    /// </remarks>
    ElementRead<string> TagName(nint window, string elementId);

    /// <summary>The element's text.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    /// ValuePattern's value when the element has one — <b>even when empty</b> —
    /// then the selected item of a Selection, then Name. Measured against
    /// Settings' search box, which is the only condition tried so far where the
    /// rules predict different answers.
    /// </remarks>
    ElementRead<string> Text(nint window, string elementId);

    /// <summary>Whether the element is enabled.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>The flag.</returns>
    ElementRead<bool> IsEnabled(nint window, string elementId);

    /// <summary>Whether the element is on screen.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>The flag.</returns>
    /// <remarks>
    /// <c>!IsOffscreen</c>. WinAppDriver's <c>ElementDisplayed</c> test scrolls a
    /// looping selector and watches two sibling items swap, which is what that
    /// property does and not what a bounds check would do.
    /// </remarks>
    ElementRead<bool> IsDisplayed(nint window, string elementId);

    /// <summary>Whether the element is selected.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>The flag.</returns>
    /// <remarks>
    /// <see langword="false"/> for an element with no SelectionItem pattern
    /// rather than an error — asserted by
    /// <c>GetElementSelectedState_UnselectableElement</c>.
    /// </remarks>
    ElementRead<bool> IsSelected(nint window, string elementId);

    /// <summary>The element's rectangle in screen coordinates.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>The bounds.</returns>
    /// <remarks>
    /// UIA's <c>BoundingRectangle</c> unchanged. This is the space synthesized
    /// mouse input works in, and the space the click guard has to compare
    /// against — never the window-relative one below.
    /// </remarks>
    ElementRead<ElementBounds> ScreenBounds(nint window, string elementId);

    /// <summary>The element's rectangle relative to the window's origin.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element id.</param>
    /// <returns>The bounds.</returns>
    /// <remarks>
    /// <b>Window-relative, not screen coordinates.</b> Measured: an element whose
    /// UIA bounding rectangle is <c>Left:257 Top:616</c> reports
    /// <c>{x:203, y:419}</c> through <c>/location</c>, and the window sits at
    /// <c>{54, 197}</c>. Width and height are unaffected, being differences.
    ///
    /// Both rectangles come from UIA rather than one from UIA and one from
    /// <c>GetWindowRect</c>, so the subtraction is exact whatever the host
    /// process's DPI awareness happens to be.
    /// </remarks>
    ElementRead<ElementBounds> WindowRelativeBounds(nint window, string elementId);
}

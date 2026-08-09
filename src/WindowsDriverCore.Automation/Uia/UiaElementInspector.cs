using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <inheritdoc cref="IElementInspector" />
public sealed class UiaElementInspector : IElementInspector
{
    private readonly IUIAutomation _automation;
    private readonly IElementResolver _resolver;

    /// <summary>Creates the inspector.</summary>
    /// <param name="automation">The UI Automation root object.</param>
    /// <param name="resolver">Turns element ids into elements.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public UiaElementInspector(IUIAutomation automation, IElementResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(resolver);

        _automation = automation;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public ElementRead<string> TagName(nint window, string elementId) =>
        Read(window, elementId, static element => UiaControlTypes.TagName(element.CurrentControlType));

    /// <inheritdoc />
    public ElementRead<string> Text(nint window, string elementId) =>
        Read(window, elementId, TextOf);

    /// <inheritdoc />
    public ElementRead<bool> IsEnabled(nint window, string elementId) =>
        Read(window, elementId, static element => element.CurrentIsEnabled != 0);

    /// <inheritdoc />
    public ElementRead<bool> IsDisplayed(nint window, string elementId) =>
        Read(window, elementId, static element => element.CurrentIsOffscreen == 0);

    /// <inheritdoc />
    public ElementRead<bool> IsSelected(nint window, string elementId) =>
        Read(window, elementId, static element =>
            Flag(element, UiaPropertyIds.IsSelectionItemPatternAvailable) &&
            Flag(element, UiaPropertyIds.SelectionItemIsSelected));

    /// <inheritdoc />
    public ElementRead<ElementBounds> ScreenBounds(nint window, string elementId) =>
        Read(window, elementId, static element => BoundsOf(element));

    /// <inheritdoc />
    public ElementRead<ElementBounds> WindowRelativeBounds(nint window, string elementId)
    {
        ElementRead<ElementBounds> screen = ScreenBounds(window, elementId);
        if (screen.Outcome != ElementReadOutcome.Read)
        {
            return screen;
        }

        // The window's own rectangle comes from UIA rather than GetWindowRect,
        // so both sides of the subtraction are measured by the same instrument.
        // Mixing UIA with Win32 here would introduce a DPI-awareness difference
        // that is zero on the developer's machine and non-zero on somebody's.
        IUIAutomationElement? root;
        try
        {
            root = _automation.ElementFromHandle(window);
        }
        catch (COMException)
        {
            return ElementRead.Failed<ElementBounds>(ElementReadOutcome.NoSuchWindow);
        }

        if (root is null)
        {
            return ElementRead.Failed<ElementBounds>(ElementReadOutcome.NoSuchWindow);
        }

        try
        {
            ElementBounds windowBounds = BoundsOf(root);
            ElementBounds bounds = screen.Value;

            return ElementRead.Success(new ElementBounds(
                bounds.X - windowBounds.X,
                bounds.Y - windowBounds.Y,
                bounds.Width,
                bounds.Height));
        }
        catch (COMException)
        {
            return ElementRead.Failed<ElementBounds>(ElementReadOutcome.NoSuchWindow);
        }
        finally
        {
            Marshal.ReleaseComObject(root);
        }
    }

    /// <summary>
    /// Resolves an id and reads one thing from the element.
    /// </summary>
    /// <remarks>
    /// Every property route is this shape, so the resolution, the outcome
    /// mapping and the COM lifetime live here once. Six copies of it is how the
    /// previous implementation ended up releasing elements on some paths and not
    /// others.
    /// </remarks>
    private ElementRead<T> Read<T>(
        nint window, string elementId, Func<IUIAutomationElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(elementId);

        using ElementLookupResult lookup = _resolver.Resolve(window, elementId);

        if (lookup.Outcome != ElementLookupOutcome.Resolved ||
            lookup.Element is not IUIAutomationElement element)
        {
            return ElementRead.Failed<T>(
                lookup.Outcome == ElementLookupOutcome.NoSuchWindow
                    ? ElementReadOutcome.NoSuchWindow
                    : ElementReadOutcome.NotFound);
        }

        try
        {
            return ElementRead.Success(read(element));
        }
        catch (COMException)
        {
            // The element resolved and then went away before the property could
            // be read. Reporting it as not found is the same answer the client
            // would have got a moment earlier, and the protocol layer turns it
            // into a stale reference.
            return ElementRead.Failed<T>(ElementReadOutcome.NotFound);
        }
    }

    /// <summary>
    /// ValuePattern's value, then a Selection's selected item, then Name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against Settings' search box, which is the condition where the
    /// candidate rules disagree: it has a Name of "Search box, Find a setting"
    /// and an empty value, and <c>/text</c> answers <c>""</c>. So an <b>empty
    /// value beats a non-empty Name</b> — the rule keys on whether the pattern
    /// exists, not on whether the string is empty. Calculator cannot show this,
    /// because nothing in it has both.
    /// </para>
    /// <para>
    /// The Selection rung comes from WinAppDriver's own
    /// <c>ElementText.GetElementText</c>, which asserts a looping selector
    /// answers <c>"00"</c>. It is not measured here — no Windows 11 app tried so
    /// far still exposes that control in a reachable state.
    /// </para>
    /// </remarks>
    private static string TextOf(IUIAutomationElement element)
    {
        if (Flag(element, UiaPropertyIds.IsValuePatternAvailable))
        {
            return element.GetCurrentPropertyValue(UiaPropertyIds.ValueValue) as string
                ?? string.Empty;
        }

        if (Flag(element, UiaPropertyIds.IsSelectionPatternAvailable) &&
            element.GetCurrentPropertyValue(UiaPropertyIds.SelectionSelection)
                is IUIAutomationElementArray selection &&
            selection.Length > 0)
        {
            IUIAutomationElement? selected = selection.GetElement(0);
            if (selected is not null)
            {
                try
                {
                    return TextOf(selected);
                }
                finally
                {
                    Marshal.ReleaseComObject(selected);
                }
            }
        }

        return element.CurrentName ?? string.Empty;
    }

    private static bool Flag(IUIAutomationElement element, int propertyId) =>
        element.GetCurrentPropertyValue(propertyId) is bool value && value;

    /// <summary>
    /// An element's rectangle in screen coordinates, rounded to whole pixels.
    /// </summary>
    /// <remarks>
    /// <c>CurrentBoundingRectangle</c> is already integral, so the conversion is
    /// exact; the cast is here because UIA types it as a rectangle of longs and
    /// the protocol emits ints.
    /// </remarks>
    private static ElementBounds BoundsOf(IUIAutomationElement element)
    {
        tagRECT rectangle = element.CurrentBoundingRectangle;

        return new ElementBounds(
            rectangle.left,
            rectangle.top,
            rectangle.right - rectangle.left,
            rectangle.bottom - rectangle.top);
    }
}

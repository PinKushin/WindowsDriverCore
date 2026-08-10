using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <inheritdoc cref="IElementInteractor" />
/// <remarks>
/// The ladder from <c>docs/CLICK-SEMANTICS.md</c>. Each rung is there because
/// something in a real suite needed it, and the order is not arbitrary: the
/// state-bearing patterns are tried before Invoke, which UI Automation defines
/// as the generic default action for controls that keep no state. See
/// <c>ClickOne</c> for why that ordering was wrong until 2026-08-09.
/// </remarks>
public sealed class UiaElementInteractor : IElementInteractor
{
    /// <summary>
    /// How many ancestors are tried when the element itself has no pattern.
    /// </summary>
    /// <remarks>
    /// Three, from field evidence: a MAUI <c>CollectionView</c> row put its
    /// <c>AutomationId</c> on a <c>Border</c> inside the item container, while
    /// the container held <c>SelectionItemPattern</c>. The id named a child with
    /// no pattern and its parent was perfectly selectable. Without this rung such
    /// an element falls straight through to the mouse, which is the original
    /// defect.
    /// </remarks>
    private const int AncestorsToTry = 3;

    private const int EditControlType = 50004;
    private const int DocumentControlType = 50030;
    private const int ExpandCollapseStateCollapsed = 0;

    private readonly IUIAutomation _automation;
    private readonly IElementResolver _resolver;

    /// <summary>Creates the interactor.</summary>
    /// <param name="automation">The UI Automation root object.</param>
    /// <param name="resolver">Turns element ids into elements.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public UiaElementInteractor(IUIAutomation automation, IElementResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(resolver);

        _automation = automation;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public ElementAction Click(nint window, string elementId) =>
        Act(window, elementId, ClickElementOrAncestor);

    /// <inheritdoc />
    public ElementAction Clear(nint window, string elementId) =>
        Act(window, elementId, static element =>
        {
            // An element with no ValuePattern has nothing to clear, and WinAppDriver
            // answers 200 for exactly that case. Reporting success for doing
            // nothing is the measured contract here, not an oversight.
            if (!Has(element, UiaPropertyIds.IsValuePatternAvailable))
            {
                return ElementAction.Performed("NoValueToClear");
            }

            return SetValueThrough(element, string.Empty)
                ? ElementAction.Performed("Value.SetValue")
                : ElementAction.Failed(ElementActionOutcome.NotInteractable);
        });

    /// <inheritdoc />
    public ElementAction SetValue(nint window, string elementId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Act(window, elementId, element =>
            Has(element, UiaPropertyIds.IsValuePatternAvailable) && SetValueThrough(element, value)
                ? ElementAction.Performed("Value.SetValue")

                // Unlike Clear, this is a failure. The caller asked for the
                // element to hold a value and it does not; saying "done" would be
                // indistinguishable from having worked.
                : ElementAction.Failed(ElementActionOutcome.NotInteractable));
    }

    /// <summary>
    /// Resolves an id and runs an action against the element.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="UiaElementInspector"/>'s read helper, and for
    /// the same reasons: the COM lifetime stays inside one call, and the outcome
    /// mapping lives in one place rather than once per route.
    /// </remarks>
    private ElementAction Act(
        nint window, string elementId, Func<IUIAutomationElement, ElementAction> action)
    {
        ArgumentNullException.ThrowIfNull(elementId);

        using ElementLookupResult lookup = _resolver.Resolve(window, elementId);

        if (lookup.Outcome != ElementLookupOutcome.Resolved ||
            lookup.Element is not IUIAutomationElement element)
        {
            return ElementAction.Failed(
                lookup.Outcome == ElementLookupOutcome.NoSuchWindow
                    ? ElementActionOutcome.NoSuchWindow
                    : ElementActionOutcome.NotFound);
        }

        try
        {
            return action(element);
        }
        catch (Exception exception) when (IsProviderRefusal(exception))
        {
            // The element went away between resolving it and acting on it, or
            // its provider refused outright. Either way the id no longer names
            // something this driver can act on.
            return ElementAction.Failed(ElementActionOutcome.NotFound);
        }
    }

    private ElementAction ClickElementOrAncestor(IUIAutomationElement element)
    {
        ScrollIntoView(element);

        ElementAction direct = ClickOne(element);
        if (direct.Outcome == ElementActionOutcome.Performed)
        {
            return direct;
        }

        // The rung that fixed the CollectionView. The id may name a child of the
        // element that actually carries the pattern.
        IUIAutomationTreeWalker walker = _automation.ControlViewWalker;
        IUIAutomationElement current = element;
        bool ownsCurrent = false;

        try
        {
            for (int level = 1; level <= AncestorsToTry; level++)
            {
                IUIAutomationElement? parent = walker.GetParentElement(current);
                if (parent is null)
                {
                    break;
                }

                if (ownsCurrent)
                {
                    Marshal.ReleaseComObject(current);
                }

                current = parent;
                ownsCurrent = true;

                ElementAction viaAncestor = ClickOne(current);
                if (viaAncestor.Outcome == ElementActionOutcome.Performed)
                {
                    return ElementAction.Performed($"ancestor:{level}/{viaAncestor.Path}");
                }
            }
        }
        finally
        {
            if (ownsCurrent)
            {
                Marshal.ReleaseComObject(current);
            }
        }

        // Nothing carried the click. The mouse path belongs here and is not
        // built yet; until it is, this reports rather than pretends.
        return ElementAction.Failed(ElementActionOutcome.NotInteractable);
    }

    /// <summary>
    /// One element, one pass down the pattern ladder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters and is not preference. A list row exposes SelectionItem; a
    /// MAUI Picker becomes a WinUI ComboBox exposing ExpandCollapse. Trying only
    /// the first would leave each of those unclickable.
    /// </para>
    /// <para>
    /// <b>The state-bearing patterns come before Invoke, and that ordering is
    /// the spec's, not a preference.</b> UI Automation: "Controls support
    /// InvokePattern if the same behavior is not exposed through another control
    /// pattern", and "Controls that do maintain state, such as check boxes and
    /// radio buttons, must instead implement IToggleProvider and
    /// ISelectionItemProvider respectively." Invoke is the generic
    /// default-action pattern, so it is the fallback, never the first choice.
    /// </para>
    /// <para>
    /// <b>This was the other way round, on the premise that "a checkbox exposes
    /// Toggle and not Invoke".</b> Measured false 2026-08-09: charmap's Win32
    /// checkbox advertises <i>both</i>, so the ladder fired Invoke and the
    /// Toggle rung was unreachable on any classic checkbox. Settings does the
    /// same with ListItems — 9 of 22 advertise Invoke alongside SelectionItem.
    /// Providers over-advertise Invoke; a client that trusts that advertisement
    /// inherits the mistake. Nothing caught it because every checkbox reachable
    /// before charmap was XAML, which advertises Toggle alone.
    /// </para>
    /// <para>
    /// Both orders behave identically on an element carrying only one of the
    /// patterns, which is why the overlap is the only condition that can
    /// distinguish them, and why the tests hunt for elements advertising two.
    /// </para>
    /// </remarks>
    private static ElementAction ClickOne(IUIAutomationElement element)
    {
        if (Invoke<IUIAutomationTogglePattern>(
            element, UiaPatternIds.Toggle, UiaPropertyIds.IsTogglePatternAvailable,
            static pattern => pattern.Toggle()))
        {
            return ElementAction.Performed("Toggle");
        }

        if (Invoke<IUIAutomationSelectionItemPattern>(
            element, UiaPatternIds.SelectionItem, UiaPropertyIds.IsSelectionItemPatternAvailable,
            static pattern => pattern.Select()))
        {
            return ElementAction.Performed("SelectionItem");
        }

        if (Invoke<IUIAutomationInvokePattern>(
            element, UiaPatternIds.Invoke, UiaPropertyIds.IsInvokePatternAvailable,
            static pattern => pattern.Invoke()))
        {
            return ElementAction.Performed("Invoke");
        }

        if (Has(element, UiaPropertyIds.IsExpandCollapsePatternAvailable))
        {
            // Focus first: a WinUI ComboBox will expand and immediately collapse
            // again if it does not have focus when the pattern is invoked.
            TryFocus(element);

            if (Invoke<IUIAutomationExpandCollapsePattern>(
                element, UiaPatternIds.ExpandCollapse, UiaPropertyIds.IsExpandCollapsePatternAvailable,
                static pattern =>
                {
                    if ((int)pattern.CurrentExpandCollapseState == ExpandCollapseStateCollapsed)
                    {
                        pattern.Expand();
                    }
                    else
                    {
                        pattern.Collapse();
                    }
                }))
            {
                return ElementAction.Performed("ExpandCollapse");
            }
        }

        // Clicking a text input means focusing it. Only for the control types
        // where that is what a click does — a blanket SetFocus() fallback is how
        // the previous implementation reported success for doing nothing.
        int controlType = element.CurrentControlType;
        if ((controlType == EditControlType || controlType == DocumentControlType) &&
            TryFocus(element))
        {
            return ElementAction.Performed("Focus");
        }

        return ElementAction.Failed(ElementActionOutcome.NotInteractable);
    }

    /// <summary>
    /// Runs an action through a pattern, if the element has it.
    /// </summary>
    /// <remarks>
    /// Availability is checked by property first. Asking for a pattern the
    /// element does not have returns null rather than throwing, but the property
    /// read is the cheaper question and keeps the null case out of the happy
    /// path.
    /// </remarks>
    private static bool Invoke<TPattern>(
        IUIAutomationElement element, int patternId, int availabilityPropertyId, Action<TPattern> use)
        where TPattern : class
    {
        if (!Has(element, availabilityPropertyId))
        {
            return false;
        }

        if (element.GetCurrentPattern(patternId) is not TPattern pattern)
        {
            return false;
        }

        try
        {
            use(pattern);
            return true;
        }
        catch (Exception exception) when (IsProviderRefusal(exception))
        {
            // The pattern is advertised but refused — a disabled control, or one
            // whose provider changed its mind. Falling through to the next rung
            // is right; claiming success is not.
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(pattern);
        }
    }

    private static bool SetValueThrough(IUIAutomationElement element, string value) =>
        Invoke<IUIAutomationValuePattern>(
            element, UiaPatternIds.Value, UiaPropertyIds.IsValuePatternAvailable,
            pattern => pattern.SetValue(value));

    private static void ScrollIntoView(IUIAutomationElement element) =>
        Invoke<IUIAutomationScrollItemPattern>(
            element, UiaPatternIds.ScrollItem, UiaPropertyIds.IsScrollItemPatternAvailable,
            static pattern => pattern.ScrollIntoView());

    private static bool TryFocus(IUIAutomationElement element)
    {
        try
        {
            element.SetFocus();
            return true;
        }
        catch (Exception exception) when (IsProviderRefusal(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an exception is a provider declining, rather than a fault here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>catch (COMException)</c> is not enough, and that is not obvious.</b>
    /// The runtime maps well-known HRESULTs to specific .NET types — E_INVALIDARG
    /// to <see cref="ArgumentException"/>, E_ACCESSDENIED to
    /// <see cref="UnauthorizedAccessException"/>, E_NOTIMPL to
    /// <see cref="NotImplementedException"/>, E_NOINTERFACE to
    /// <see cref="InvalidCastException"/> — and passes a
    /// <see cref="COMException"/> <i>only</i> when the HRESULT is one it does not
    /// recognise.
    /// </para>
    /// <para>
    /// UIA's own codes (UIA_E_ELEMENTNOTAVAILABLE and friends) are custom, so
    /// they do arrive as COMException, which is why this hole stayed invisible:
    /// every failure this driver had provoked until now was a UIA-specific one.
    /// Measured 2026-08-09 — <c>SetFocus()</c> on a WPF TextBox threw
    /// <c>ArgumentException: Value does not fall within the expected range</c>,
    /// which escaped the catch and took the whole click with it instead of
    /// falling through to the next rung.
    /// </para>
    /// <para>
    /// <b>Two are deliberately absent.</b> <see cref="OutOfMemoryException"/>
    /// (E_OUTOFMEMORY) is a real machine failure, and
    /// <see cref="NullReferenceException"/> (E_POINTER) means this code passed a
    /// bad pointer. Swallowing either would hide a defect rather than handle a
    /// refusal.
    /// </para>
    /// </remarks>
    private static bool IsProviderRefusal(Exception exception) =>
        exception is COMException
            or ArgumentException
            or NotImplementedException
            or UnauthorizedAccessException
            or InvalidCastException;

    private static bool Has(IUIAutomationElement element, int availabilityPropertyId) =>
        element.GetCurrentPropertyValue(availabilityPropertyId) is bool available && available;
}

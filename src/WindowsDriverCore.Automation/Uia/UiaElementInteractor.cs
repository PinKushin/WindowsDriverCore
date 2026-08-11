using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using WindowsDriverCore.Platform.Windows;

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
    private readonly IPointerInput? _pointer;
    private readonly IWindowLocator? _windows;
    private readonly IKeyboardInput? _keyboard;

    /// <summary>Creates the interactor.</summary>
    /// <param name="automation">The UI Automation root object.</param>
    /// <param name="resolver">Turns element ids into elements.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <param name="mouse">
    /// Real mouse input for the last rung. Optional: without it the ladder ends
    /// by refusing, which is what it did before the rung existed.
    /// </param>
    /// <param name="keyboard">
    /// Real keyboard input. Optional: without it SendKeys refuses rather than
    /// falling back to SetValue, because replacing a field's contents is a
    /// different operation from typing into it and the caller asked for typing.
    /// </param>
    /// <param name="windows">
    /// Supplies the window rectangle the click point is checked against. Optional
    /// for the same reason — and the mouse rung is skipped entirely without it,
    /// because an unguarded coordinate click is the defect this project exists
    /// to fix.
    /// </param>
    public UiaElementInteractor(
        IUIAutomation automation,
        IElementResolver resolver,
        IPointerInput? mouse = null,
        IWindowLocator? windows = null,
        IKeyboardInput? keyboard = null)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(resolver);

        _automation = automation;
        _resolver = resolver;
        _pointer = mouse;
        _windows = windows;
        _keyboard = keyboard;
    }

    /// <inheritdoc />
    public ElementAction Click(nint window, string elementId) =>
        Act(window, elementId, element => ClickElementOrAncestor(window, element));

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

    /// <inheritdoc />
    public ElementAction SendKeys(nint window, string elementId, string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (_keyboard is null)
        {
            return ElementAction.Failed(ElementActionOutcome.NotInteractable);
        }

        return Act(window, elementId, element =>
        {
            // Foreground FIRST, then focus. UI Automation's SetFocus fails
            // against a control in a background window even when it reports
            // focusable, enabled and on screen — measured 2026-08-10 — so
            // focusing without this simply does not work.
            _windows?.BringToForeground(window);

            // Then focus, because keystrokes go wherever focus is: typing
            // without moving it would type into whatever the user last clicked.
            //
            // KNOWN GAP: WPF's provider refuses SetFocus on a perfectly
            // focusable TextBox even in a foregrounded window. Measured
            // 2026-08-10 with foregrounding confirmed working
            // (foregrounded=True, target==actual) and SendInput confirmed
            // working (typeWorks=True), which leaves this as the only branch
            // that can fail. Falling back to a CLICK was tried and reverted: it
            // drags the whole ladder in, has side effects on non-text elements,
            // and made an unrelated test take 30 seconds. See
            // docs/LIMITATIONS.md.
            // FOCUS IS ATTEMPTED, NOT REQUIRED, and the recording is why.
            //
            // error.element.sendKeysDisabled.ClearMemoryButton has WinAppDriver
            // typing at a DISABLED Calculator button and answering 200 status 0.
            // Refusing when a provider declines SetFocus therefore diverges from
            // the reference on a case the suite exercises - and it is what left
            // an Action Center pane on screen, because the Escape that dismisses
            // it is sent to the pane itself.
            //
            // The path records which happened, so a transcript still shows that
            // the keystrokes went somewhere nobody focused. The window was
            // foregrounded above either way, so "somewhere" is the application
            // under test rather than the desktop.
            bool focused = TryFocus(element);
            string path = focused ? "keys" : "keys (unfocused)";

            return _keyboard.Type(keys)
                ? ElementAction.Performed(path)
                : ElementAction.Failed(ElementActionOutcome.NotInteractable);
        });
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

    private ElementAction ClickElementOrAncestor(nint window, IUIAutomationElement element)
    {
        ScrollIntoView(element);

        // Foreground before any rung runs. UI Automation refuses SetFocus
        // against a background window — measured 2026-08-10 — so the Focus rung
        // is unreachable without this, and a mouse click on a window that is
        // behind another one lands on whatever is in front.
        _windows?.BringToForeground(window);

        // A disabled element gets the mouse and nothing else — and above all, no
        // climb.
        //
        // Measured against Alarms & Clock on Windows 10, 2026-08-10. Its
        // "Add new alarm" button goes disabled once the alarm list hits the
        // application's cap. InvokePattern.Invoke() threw, so the ladder fell
        // through to the ancestor walk, reached AlarmCollectionPageCommandBar —
        // which advertises Toggle and ExpandCollapse — and toggled the app bar.
        // The driver then answered status 0: the client is told "Add new alarm
        // was clicked" while the application opened and closed its overflow menu.
        // A successful click and a click that toggled something else look
        // identical from the wire, so nothing downstream can catch it.
        //
        // **The climb is the defect. Refusing the click was our own idea, and it
        // was wrong** — measured 2026-08-11: twelve compatibility-suite tests
        // passed in every run before the refusal and failed in every run after
        // it, GetElementEnabledState because it clicks a deliberately disabled
        // button and expects that to work, and eleven StaleElement tests because
        // they reach their subject through a disabled AddAlarmButton.
        //
        // A real user clicking a disabled button gets a click that lands and does
        // nothing. That is what this dispatches, and reporting it as performed is
        // honest: the click happened. What must not happen is inventing a
        // different target for it.
        if (IsDisabled(element))
        {
            return ClickWithTheMouse(element);
        }

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

        // Last rung: real mouse input, guarded.
        return ClickWithTheMouse(element);
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

    /// <summary>
    /// The last rung: a real mouse click, refused if it would leave the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The guard is the point, not the click.</b> WinAppDriver's mouse path
    /// dispatches at screen coordinates without checking where they land, and a
    /// click that leaves the target window is input delivered to another
    /// application — on a developer's machine it opens whatever is underneath,
    /// on CI it silently accomplishes nothing. That is the defect documented in
    /// docs/CLICK-SEMANTICS.md with a reproduction and a measured before/after.
    /// </para>
    /// <para>
    /// <b>The rectangle is re-read here, after the scroll.</b> ScrollIntoView ran
    /// at the top of the ladder, so a rect captured before it is stale by
    /// exactly the amount the element moved — which is the amount that matters.
    /// </para>
    /// <para>
    /// Without a pointer or a window locator the rung is skipped and the ladder
    /// refuses, because an UNGUARDED coordinate click is worse than no click.
    /// </para>
    /// </remarks>
    private ElementAction ClickWithTheMouse(IUIAutomationElement element)
    {
        if (_pointer is null || _windows is null)
        {
            return ElementAction.Failed(ElementActionOutcome.NotInteractable);
        }

        tagRECT rect;
        try
        {
            rect = element.CurrentBoundingRectangle;
        }
        catch (Exception exception) when (IsProviderRefusal(exception))
        {
            return ElementAction.Failed(ElementActionOutcome.NotInteractable);
        }

        // A zero rectangle means the element has no on-screen presence at all.
        // Clicking its "centre" would be a click at the desktop origin.
        if (rect.right <= rect.left || rect.bottom <= rect.top)
        {
            return ElementAction.Failed(ElementActionOutcome.NotInteractable);
        }

        int x = rect.left + ((rect.right - rect.left) / 2);
        int y = rect.top + ((rect.bottom - rect.top) / 2);

        nint window = element.CurrentNativeWindowHandle;
        if (window == 0)
        {
            // The element has no window of its own — almost all of them. The
            // guard needs the top-level window, which the caller's search root
            // is, so fall back to the ancestor that does have one.
            window = TopLevelWindowOf(element);
        }

        WindowBounds? bounds = _windows.GetBounds(window);
        if (bounds is null)
        {
            return ElementAction.Failed(ElementActionOutcome.NotInteractable);
        }

        bool inside = x >= bounds.X && x < bounds.X + bounds.Width &&
                      y >= bounds.Y && y < bounds.Y + bounds.Height;

        if (!inside)
        {
            // Refused, loudly, rather than dispatched. This is the case that
            // turns an invisible flake into a diagnosable error.
            return ElementAction.Failed(ElementActionOutcome.NotInteractable);
        }

        // And the question that actually decides where the click lands: is this
        // window the one DRAWN at that point? Being inside the rectangle is not
        // the same thing — a covered window satisfies it and receives nothing.
        //
        // Raising the window above is best-effort by necessity:
        // SetForegroundWindow refuses when the calling process does not hold
        // foreground rights, and its result was being discarded. Rather than
        // trust that, this checks the outcome.
        //
        // MEASURED 2026-08-11: MouseDoubleClick and MouseDownMoveUp passed at
        // c26e4d3 and failed in BOTH runs at 0cdadc6 — the commit that first let
        // the suite open File Explorer sessions, whose windows outlive the session
        // and sit over the target. Both failures are "the effect did not happen",
        // which is what a click delivered to somebody else's window looks like.
        if (!_windows.OwnsThePointAt(x, y, window))
        {
            // One re-attempt, because raising the window is the only action that
            // can change this answer, and the ladder's earlier attempt was made
            // before scrolling and before any of the pattern rungs ran. Bounded at
            // one: a second failure means something is genuinely in front, and
            // retrying a losing race is how flake gets built in.
            _windows.BringToForeground(window);

            if (!_windows.OwnsThePointAt(x, y, window))
            {
                return ElementAction.Failed(ElementActionOutcome.NotInteractable);
            }
        }

        return _pointer.ClickAt(x, y)
            ? ElementAction.Performed("mouse")
            : ElementAction.Failed(ElementActionOutcome.NotInteractable);
    }

    /// <summary>The nearest ancestor that owns a real window.</summary>
    private nint TopLevelWindowOf(IUIAutomationElement element)
    {
        IUIAutomationTreeWalker walker = _automation.ControlViewWalker;
        IUIAutomationElement? current = element;

        for (int level = 0; level < 12 && current is not null; level++)
        {
            nint handle = current.CurrentNativeWindowHandle;
            if (handle != 0)
            {
                return handle;
            }

            current = walker.GetParentElement(current);
        }

        return 0;
    }

    private static bool Has(IUIAutomationElement element, int availabilityPropertyId) =>
        element.GetCurrentPropertyValue(availabilityPropertyId) is bool available && available;

    /// <summary>Whether the provider reports the element as disabled.</summary>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> only when it says so plainly.</returns>
    /// <remarks>
    /// A provider that will not answer is not the same as one answering "no".
    /// If the read fails, the ladder runs as before and the element gets its
    /// chance — refusing on a failed read would turn every awkward provider into
    /// an unclickable element, which is a much worse trade than the bug this
    /// guards.
    /// </remarks>
    private static bool IsDisabled(IUIAutomationElement element)
    {
        try
        {
            return element.GetCurrentPropertyValue(UiaPropertyIds.IsEnabled) is bool enabled
                && !enabled;
        }
        catch (Exception failure) when (IsProviderRefusal(failure))
        {
            return false;
        }
    }
}

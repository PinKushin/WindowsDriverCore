using System;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace WindowsDriverCore.TestApp.Wpf;

/// <summary>
/// Records which UI Automation pattern was last invoked on a control.
/// </summary>
/// <remarks>
/// <b>This is the reason the application exists.</b> Every click test in this
/// repository asserted on <c>ElementAction.Path</c> — the driver's own report of
/// which rung fired. A driver that reported the wrong rung would pass every one
/// of them. Here the <i>application</i> says which pattern it received, so the
/// measurement is independent of the thing being measured.
/// </remarks>
internal static class PatternLog
{
    private static string _last = "none";

    public static event Action? Changed;

    public static string Last => _last;

    public static void Record(string pattern)
    {
        _last = pattern;
        Changed?.Invoke();
    }
}

/// <summary>
/// A check box that advertises <b>both</b> Toggle and Invoke.
/// </summary>
/// <remarks>
/// <para>
/// Not a contrived shape. charmap's "Advanced view" check box does exactly this
/// — measured 2026-08-09 — because the Win32 provider bridges legacy controls
/// and over-advertises Invoke. UI Automation's own guidance says it should not:
/// "Controls that do maintain state, such as check boxes and radio buttons, must
/// instead implement IToggleProvider".
/// </para>
/// <para>
/// <b>Invoke deliberately does not change the check state.</b> That asymmetry is
/// the instrument: if the ladder picks the wrong pattern, the state does not
/// move and <c>lastPattern</c> reads "Invoke". If both did the same thing, the
/// two hypotheses would predict the same observation and the test could not
/// fail.
/// </para>
/// </remarks>
internal sealed class DualPatternCheckBox : CheckBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DualPatternPeer(this);

    private sealed class DualPatternPeer : CheckBoxAutomationPeer, IInvokeProvider, IToggleProvider
    {
        private readonly DualPatternCheckBox _owner;

        public DualPatternPeer(DualPatternCheckBox owner)
            : base(owner) => _owner = owner;

        public ToggleState ToggleState => _owner.IsChecked == true
            ? ToggleState.On
            : ToggleState.Off;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.Invoke or PatternInterface.Toggle
                ? this
                : base.GetPattern(patternInterface);

        public void Invoke() => PatternLog.Record("Invoke");

        public void Toggle()
        {
            PatternLog.Record("Toggle");
            _owner.IsChecked = _owner.IsChecked != true;
        }
    }
}

/// <summary>
/// A container that carries Toggle but does <b>not</b> respond to the mouse.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one subject that can tell a climb from a click.</b> The obvious host —
/// a real <see cref="CheckBox"/> wrapping a disabled child — cannot: a disabled
/// element does not consume mouse input, so the click routes to the check box and
/// toggles it. Firing Toggle on the ancestor and clicking the child's coordinates
/// then produce the <i>same</i> observation, and the test passes whichever the
/// driver did. Measured 2026-08-11, on exactly that subject.
/// </para>
/// <para>
/// This one has no mouse handling at all. A coordinate click inside it does
/// nothing; <see cref="IToggleProvider.Toggle"/> moves <see cref="ToggleState"/>.
/// The two hypotheses now predict different observations, which is the only
/// property that makes the assertion worth making.
/// </para>
/// <para>
/// Custom rather than a real control type, because that is what it is: a
/// container advertising a pattern it does not implement in its own input
/// handling — the shape AlarmCollectionPageCommandBar has.
/// </para>
/// </remarks>
internal sealed class InertToggleHost : ContentControl
{
    private bool _on;

    protected override AutomationPeer OnCreateAutomationPeer() => new InertTogglePeer(this);

    private sealed class InertTogglePeer : FrameworkElementAutomationPeer, IToggleProvider
    {
        private readonly InertToggleHost _owner;

        public InertTogglePeer(InertToggleHost owner)
            : base(owner) => _owner = owner;

        public ToggleState ToggleState => _owner._on ? ToggleState.On : ToggleState.Off;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle
                ? this
                : base.GetPattern(patternInterface);

        public void Toggle()
        {
            PatternLog.Record("Toggle:inertHost");
            _owner._on = !_owner._on;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Custom;

        // Explicit, because the driver climbs the CONTROL view. A host the walker
        // skips would make the climb unreachable and the test vacuous.
        protected override bool IsControlElementCore() => true;

        protected override string GetClassNameCore() => nameof(InertToggleHost);
    }
}

/// <summary>
/// A radio button that advertises <b>both</b> SelectionItem and Invoke.
/// </summary>
/// <remarks>
/// <para>
/// Also not contrived: 9 of Settings' 22 list items advertise Invoke alongside
/// SelectionItem. A radio button is the shape UI Automation's own guidance names
/// for this — "radio buttons must instead implement ISelectionItemProvider" —
/// and WPF's <see cref="RadioButtonAutomationPeer"/> supplies that half already,
/// so the only thing added here is the Invoke the spec says should be absent.
/// </para>
/// <para>
/// Selection state is the measurement, and Invoke does nothing observable beyond
/// recording itself, so a ladder that picks Invoke leaves the button unselected.
/// </para>
/// </remarks>
internal sealed class DualPatternRadioButton : RadioButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DualPatternPeer(this);

    private sealed class DualPatternPeer : RadioButtonAutomationPeer, IInvokeProvider
    {
        public DualPatternPeer(DualPatternRadioButton owner)
            : base(owner)
        {
        }

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke
                ? this
                : base.GetPattern(patternInterface);

        public void Invoke() => PatternLog.Record("Invoke");
    }
}

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowsDriverCore.TestApp.Wpf;

/// <summary>
/// A window containing one deterministic subject for every rung of the click
/// ladder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the real applications were not controls.</b> Settings
/// supplied subjects for five rungs, but which subjects exist depends on which
/// page Settings happens to reopen on, so two runs of the same test measured
/// different things and one reported "no subject" as a skip — which reads as a
/// pass. Calculator has one pattern in the whole tree. charmap has a toggle but
/// nothing else.
/// </para>
/// <para>
/// Everything here is addressed by <c>AutomationId</c>, is present on every run,
/// and never moves.
/// </para>
/// <para>
/// <b>The dual-pattern controls are the point.</b> On an element advertising one
/// pattern, every possible ladder order predicts the same observation — the
/// condition cannot discriminate. Only an element advertising two can tell a
/// correct order from a wrong one, and real providers do over-advertise: charmap
/// on Win32, Settings on XAML.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // WPF MUST BE TOLD TO CONSUME WM_POINTER, or it cannot observe injected
        // touch at all.
        //
        // By default WPF takes touch through the WISP stylus stack, which expects
        // a real digitiser. Injected input arrives as WM_POINTER instead, WPF
        // ignores it, and Windows promotes it to a mouse event - so the subject
        // sees a mouse and reports one. Measured 2026-08-11: a synthetic tap that
        // the system accepted arrived here as "mouse", with TouchDown never
        // firing.
        //
        // This switch makes WPF process WM_POINTER directly. It changes what the
        // SUBJECT can observe, not what the driver sends: a UWP application
        // consumes WM_POINTER natively and needs nothing, which is why
        // WinAppDriver's own touch tests pass against Alarms & Clock.
        AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.EnablePointerSupport", true);

        Application application = new();
        application.Run(BuildWindow());
    }

    private static Window BuildWindow()
    {
        StackPanel panel = new() { Margin = new Thickness(12) };

        // Invoke, uncontested. The control case: a button really does maintain
        // no state, so Invoke is the correct pattern and must stay chosen.
        panel.Children.Add(Identified(new Button { Content = "Invoke only" }, "invokeOnly"));

        // Toggle, uncontested.
        panel.Children.Add(Identified(new CheckBox { Content = "Toggle only" }, "toggleOnly"));

        // Toggle contested by Invoke. charmap's shape.
        panel.Children.Add(Identified(
            new DualPatternCheckBox { Content = "Toggle and Invoke" }, "toggleAndInvoke"));

        // SelectionItem contested by Invoke. Settings' shape.
        panel.Children.Add(Identified(
            new DualPatternRadioButton { Content = "SelectionItem and Invoke" },
            "selectionItemAndInvoke"));

        // ExpandCollapse.
        ComboBox combo = new();
        combo.Items.Add("first");
        combo.Items.Add("second");
        panel.Children.Add(Identified(combo, "expandCollapse"));

        // Focus: clicking a text input means focusing it.
        panel.Children.Add(Identified(new TextBox { Width = 200 }, "edit"));

        // The ancestor walk: a pattern-less element exactly one level inside a
        // control that has a pattern. This is the rung with field evidence
        // behind it — a MAUI CollectionView whose rows were bare labels.
        TextBlock insideButton = new() { Text = "no pattern of its own" };
        Identified(insideButton, "patternlessChild");
        panel.Children.Add(Identified(new Button { Content = insideButton }, "ancestorWithInvoke"));

        // A DISABLED element inside a container that does carry a pattern.
        // Alarms & Clock's shape exactly: AddAlarmButton sits disabled inside
        // AlarmCollectionPageCommandBar, which advertises Toggle and
        // ExpandCollapse. Measured 2026-08-10 — Invoke threw because the button
        // was disabled, the ladder climbed one level, toggled the command bar,
        // and answered status 0. The app bar opened and closed while the driver
        // reported that "Add new alarm" had been clicked.
        //
        // The host is INERT to the mouse on purpose. It was a CheckBox, and that
        // could not discriminate: a disabled child does not consume mouse input,
        // so the coordinate click routed to the check box and toggled it — the
        // same observation a wrong climb produces. See InertToggleHost.
        Button disabledInside = new() { Content = "disabled", IsEnabled = false };
        Identified(disabledInside, "disabledInsideToggle");
        panel.Children.Add(Identified(
            new InertToggleHost { Content = disabledInside }, "toggleHostingDisabled"));

        // The refusal: pattern-less, and no ancestor within three levels carries
        // a pattern either. Border and StackPanel expose no patterns, so the
        // ladder must run out and say so rather than report success.
        Border bare = new()
        {
            Child = Identified(new TextBlock { Text = "nothing can click this" }, "patternlessOrphan"),
        };
        panel.Children.Add(bare);

        // TOUCH, WHICH A MOUSE CANNOT FAKE.
        //
        // A Button raises Click for touch and for a mouse alike, so a test that
        // asserted on Click would pass just as well if the driver quietly sent a
        // mouse event instead of a real contact - and "asked for touch, received
        // a mouse" is precisely the substitution worth catching.
        //
        // TouchDown only fires for a real touch contact, so the subject records
        // which kind of input actually arrived and puts it in its own automation
        // NAME, where a test can read it without the driver mediating.
        // A BUTTON, NOT A BORDER, AND THAT IS NOT CosmETIC.
        //
        // The first version of this was a Border, and no test could find it: a
        // bare Border has no automation peer, so it is not a control element and
        // FindAll - which walks the CONTROL view - cannot see it. Measured on
        // Calculator the same day: 52 of 125 elements are invisible to a find for
        // exactly this reason.
        //
        // PREVIEW events, because a Button handles touch itself and converts it
        // to a click; the preview pass fires before that happens, so the subject
        // records which kind of input actually ARRIVED rather than what the
        // control made of it.
        // Reported through CONTENT, not AutomationProperties.Name. A Button's
        // UIA name follows its content, and an explicit name set after the
        // automation peer already exists is not guaranteed to refresh - which
        // would make this fixture fail for a reason that has nothing to do with
        // the input under test.
        Button touchTarget = new() { Content = "no input yet", Height = 44 };
        Identified(touchTarget, "touchTarget");

        // ACCUMULATED, NOT OVERWRITTEN, because Windows PROMOTES touch to mouse.
        //
        // A real touch contact raises TouchDown and then, for applications that
        // do not consume it, Windows synthesises a mouse event too - so MouseDown
        // fires afterwards. Handlers that each assign Content would leave the LAST
        // one showing, and the subject would report "mouse" for input that
        // genuinely arrived as touch. Measured 2026-08-11: that is exactly what it
        // did, and it read as an injection failure for twenty minutes.
        //
        // So every kind seen is kept. "TOUCH mouse" is the truth about a promoted
        // contact; "mouse" alone means no touch ever arrived.
        SortedSet<string> seen = [];
        void Saw(string kind)
        {
            if (seen.Add(kind))
            {
                touchTarget.Content = string.Join(" ", seen);
            }
        }

        // STYLUS IS NOT PEN. WPF raises StylusDown for touch as well as for a
        // pen - the stylus stack is the abstraction over both - so labelling it
        // "PEN" would report pen input for a finger. Measured 2026-08-11 against
        // WinAppDriver's own /touch/click, which produced "PEN TOUCH mouse" on
        // the first version of this subject and would have supported a claim that
        // it injects pen.
        //
        // The tablet device type is what actually separates them.
        touchTarget.PreviewTouchDown += (_, _) => Saw("TOUCH");
        touchTarget.PreviewMouseDown += (_, _) => Saw("mouse");
        touchTarget.PreviewStylusDown += (_, e) =>
            Saw(e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus
                ? "PEN"
                : "stylus-of-touch");

        panel.Children.Add(touchTarget);

        // What the application itself saw. Read by the tests instead of the
        // driver's own report of which rung fired.
        TextBlock report = new() { Text = PatternLog.Last };
        Identified(report, "lastPattern");
        PatternLog.Changed += () => report.Dispatcher.Invoke(() => report.Text = PatternLog.Last);
        panel.Children.Add(report);

        return new Window
        {
            Title = "WindowsDriverCore Test Subject",
            Width = 420,
            Height = 460,
            Content = panel,
        };
    }

    /// <summary>Gives an element a stable automation id and returns it.</summary>
    private static T Identified<T>(T element, string automationId)
        where T : UIElement
    {
        AutomationProperties.SetAutomationId(element, automationId);
        return element;
    }
}

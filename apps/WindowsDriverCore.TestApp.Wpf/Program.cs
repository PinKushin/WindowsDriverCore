using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

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

        // The refusal: pattern-less, and no ancestor within three levels carries
        // a pattern either. Border and StackPanel expose no patterns, so the
        // ladder must run out and say so rather than report success.
        Border bare = new()
        {
            Child = Identified(new TextBlock { Text = "nothing can click this" }, "patternlessOrphan"),
        };
        panel.Children.Add(bare);

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

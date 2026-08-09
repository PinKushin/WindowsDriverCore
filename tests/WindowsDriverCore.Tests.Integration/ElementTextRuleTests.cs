using System;
using System.Collections.Generic;
using System.Diagnostics;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The one rule about <c>/text</c> that Calculator cannot test.
/// </summary>
/// <remarks>
/// <para>
/// <b>The condition is the whole test.</b> "ValuePattern's value, else Name" and
/// "always Name" predict the same string for every element in Calculator,
/// because nothing there has both a Name and a ValuePattern. Asserting against
/// Calculator therefore proves nothing about the rule, however many buttons it
/// checks — the classic insensitive-condition failure.
/// </para>
/// <para>
/// Settings' search box has a Name of "Search box, Find a setting" and a value
/// that starts empty, so the two rules disagree: one predicts <c>""</c> and the
/// other predicts the Name. Measured against real WinAppDriver, the answer is
/// <c>""</c> — <b>an empty value beats a non-empty Name.</b>
/// </para>
/// <para>
/// This fixture exists in its own file, and launches its own application,
/// because that condition is not available anywhere in the Calculator fixture
/// and hiding it there would make it look optional.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class ElementTextRuleTests
{
    private const string SettingsAumid =
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchSettings()
    {
        WindowLocator windows = new();
        ApplicationLauncher launcher = new(new MainWindowWaiter(TimeProvider.System), windows);
        CUIAutomationClass automation = new();
        _finder = new UiaElementFinder(automation);
        _inspector = new UiaElementInspector(automation, new UiaElementResolver(automation));

        LaunchResult launched = launcher.Launch(new ApplicationTarget(SettingsAumid, null, null));
        if (launched.Application is null)
        {
            Assert.Ignore($"Settings is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;
    }

    [OneTimeTearDown]
    public void CloseSettings()
    {
        foreach (Process process in Process.GetProcessesByName("SystemSettings"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [Test]
    public void Text_PrefersAnEmptyValuePattern_OverANonEmptyName()
    {
        // Wait for content, do not assume it. Settings has a window well before
        // it has controls, and the gap widens under load.
        string searchBox = UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.ControlType, "Edit")[0];

        // The premise, asserted rather than assumed. If the search box ever
        // stops having a Name, or starts non-empty, this test stops being an
        // experiment and would silently agree with both hypotheses.
        _inspector.TagName(_window, searchBox).Value.ShouldBe("ControlType.Edit");

        ElementRead<string> text = _inspector.Text(_window, searchBox);

        text.Outcome.ShouldBe(ElementReadOutcome.Read);
        text.Value.ShouldBe(
            string.Empty,
            "an empty ValuePattern value beats a non-empty Name — measured against WinAppDriver");
    }

    [Test]
    public void Text_OfAnElementWithNoValuePattern_IsStillItsName()
    {
        // The control, in the same window at the same moment. Without it, an
        // implementation that returned "" for everything would pass the test
        // above.
        IReadOnlyList<string> buttons = UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.ControlType, "Button");

        ElementRead<string> text = _inspector.Text(_window, buttons[0]);

        text.Outcome.ShouldBe(ElementReadOutcome.Read);
        text.Value.ShouldNotBeNullOrEmpty("a titlebar button has a Name and no ValuePattern");
    }
}

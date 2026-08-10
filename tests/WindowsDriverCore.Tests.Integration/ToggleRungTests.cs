using System;
using System.Linq;
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
/// The <c>Toggle</c> rung, against a classic Win32 checkbox.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last rung without a subject.</b> Settings was surveyed rather than
/// guessed at and exposes no toggle anywhere — 6 Buttons, 1 ComboBox, 22
/// ListItems, 1 Edit, 22 Text, 20 Group, 3 Hyperlink, and not one thing
/// advertising <c>IsTogglePatternAvailable</c>. Calculator has none either.
/// </para>
/// <para>
/// It has to be covered somewhere, because <b>a checkbox exposes Toggle and NOT
/// Invoke</b>. A ladder that stopped at Invoke would leave every checkbox in
/// every application unclickable, and no test in this repository would have
/// noticed.
/// </para>
/// <para>
/// <b>charmap is deliberately a classic Win32 application, not XAML.</b>
/// Everything else exercising this driver — Calculator, Settings — is XAML, so
/// every measurement taken so far came through one UIA provider. charmap's
/// "Advanced view" checkbox is served by the Win32 provider (<c>FrameworkId</c>
/// <c>Win32</c>), which is a different implementation of the same interfaces.
/// A ladder that worked only against XAML would be a ladder that worked against
/// one provider.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class ToggleRungTests
{
    private const string CharmapPath = @"C:\Windows\System32\charmap.exe";

    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private UiaElementInteractor _interactor = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchCharmap()
    {
        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);
        _interactor = new UiaElementInteractor(automation, resolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(CharmapPath, null, null));

        if (launched.Application is null)
        {
            Assert.Ignore($"charmap is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;

        // A launched application has a window before it has a control tree.
        // Settling on the checkbox rather than on the window keeps the wait tied
        // to the thing under test.
        string? checkbox = FirstToggle();
        if (checkbox is not null)
        {
            UiSettle.UntilBoundsAreStable(_inspector, _window, checkbox);
        }
    }

    [OneTimeTearDown]
    public void CloseCharmap() => AppLifetime.KillAll("charmap");

    /// <summary>The first element in the window advertising TogglePattern.</summary>
    /// <remarks>
    /// Chosen by advertised pattern rather than by name, like every other rung
    /// test, so a Windows update that renames "Advanced view" does not silently
    /// turn this into a test of nothing.
    /// </remarks>
    private string? FirstToggle()
    {
        foreach (string controlType in new[] { "CheckBox", "Button", "MenuItem" })
        {
            FindResult found = _finder.FindAll(_window, LocatorKind.ControlType, controlType);

            string? toggle = found.ElementIds.FirstOrDefault(id =>
                _inspector.Attribute(_window, id, "IsTogglePatternAvailable").Value == "True");

            if (toggle is not null)
            {
                return toggle;
            }
        }

        return null;
    }

    [Test]
    public void AToggle_IsClickedThroughTogglePattern_NotInvoke()
    {
        string? toggle = FirstToggle();

        // Not Assert.Ignore. Settings genuinely has no toggle, so skipping there
        // was honest; charmap is launched precisely because it does have one, and
        // a skip here would mean the rung is uncovered while reading as a pass.
        toggle.ShouldNotBeNull("charmap must expose a toggle, or this fixture is pointless");

        string before = _inspector.Attribute(_window, toggle, "Toggle.ToggleState").Value ?? "?";

        ElementAction click = _interactor.Click(_window, toggle);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);

        // The path, not merely success. Toggle and a fallback to Invoke both
        // report Performed, and separating them is the entire reason the ladder
        // has an order.
        // charmap's checkbox advertises InvokePattern AS WELL, which the Win32
        // provider should not do: "Controls that do maintain state, such as check
        // boxes and radio buttons, must instead implement IToggleProvider". The
        // ladder must prefer the state-bearing pattern over the generic one, so
        // this asserts Toggle even though Invoke is also on offer and also works.
        click.Path.ShouldBe("Toggle");

        // Application state, not the driver's report of it. The failure this
        // ladder exists to fix was a click that returned success and ran no
        // handler.
        string after = _inspector.Attribute(_window, toggle, "Toggle.ToggleState").Value ?? "?";

        after.ShouldNotBe(before, "the toggle state must change, or no handler ran");

        // Put it back, so the machine is left as it was found.
        _interactor.Click(_window, toggle);
    }

    [Test]
    public void TheToggleIsServedByADifferentProviderThanTheOtherRungs()
    {
        // The reason charmap rather than another XAML application. Every other
        // measurement in this repository came through the XAML provider; this
        // one asserts that at least one rung is exercised against Win32's, so
        // "the ladder works" is not a statement about a single provider.
        string? toggle = FirstToggle();
        toggle.ShouldNotBeNull();

        _inspector.Attribute(_window, toggle, "FrameworkId").Value
            .ShouldBe("Win32", "charmap is a classic application, not XAML");
    }

    [Test]
    [Explicit("Diagnostic: prints what charmap actually exposes.")]
    public void SurveyWhatThisApplicationExposes()
    {
        string[] controlTypes =
        [
            "Button", "CheckBox", "RadioButton", "ComboBox", "ListItem",
            "Edit", "Text", "Group", "List", "MenuItem", "Pane",
        ];

        string[] patterns =
        [
            "IsInvokePatternAvailable", "IsTogglePatternAvailable",
            "IsSelectionItemPatternAvailable", "IsExpandCollapsePatternAvailable",
            "IsValuePatternAvailable",
        ];

        int surveyed = 0;

        foreach (string controlType in controlTypes)
        {
            FindResult found = _finder.FindAll(_window, LocatorKind.ControlType, controlType);
            if (found.ElementIds.Count == 0)
            {
                continue;
            }

            surveyed += found.ElementIds.Count;

            string advertised = string.Join("  ", patterns
                .Select(pattern => (Pattern: pattern, Count: found.ElementIds.Count(id =>
                    _inspector.Attribute(_window, id, pattern).Value == "True")))
                .Where(entry => entry.Count > 0)
                .Select(entry => $"{entry.Pattern[2..^16]}={entry.Count}"));

            TestContext.Out.WriteLine(
                $"{controlType,-12} {found.ElementIds.Count,3}   {advertised}");
        }

        // A survey that found nothing is a broken survey, not an empty
        // application, and without this it would print nothing and pass.
        surveyed.ShouldBeGreaterThan(0, "the survey itself must reach the tree");
    }
}

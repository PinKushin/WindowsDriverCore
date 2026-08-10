using System;
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
/// The click ladder against a subject built for the purpose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two things this can do that Settings and charmap cannot.</b>
/// </para>
/// <para>
/// First, <b>the subjects are deterministic</b>. Settings supplies whichever
/// controls its last-opened page happens to have, so the same test measured a
/// real defect on one run and reported "no subject" — a skip, which reads as a
/// pass — on the next. Everything here is present every time and addressed by
/// <c>AutomationId</c>.
/// </para>
/// <para>
/// Second, and more important, <b>the application reports which pattern it
/// received</b>. Every other click test in this repository asserts on
/// <c>ElementAction.Path</c>, which is the driver's own account of what it did:
/// a driver that fired Invoke and labelled it "Toggle" passes all of them. Here
/// the label comes from the other side of the COM boundary.
/// </para>
/// <para>
/// The dual-pattern controls are the only conditions that can distinguish a
/// correct ladder order from a wrong one. On an element advertising a single
/// pattern, every order predicts the same observation.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class LadderAgainstOwnSubjectTests
{
    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private UiaElementInteractor _interactor = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchTestApp()
    {
        string? path = TestApp.Path;
        if (path is null)
        {
            Assert.Ignore("The WPF test subject has not been built.");
        }

        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);
        _interactor = new UiaElementInteractor(automation, resolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(path, null, null));

        if (launched.Application is null)
        {
            // Fail rather than ignore. This application is built by this
            // solution, so it not launching is a defect here, not an absent
            // dependency — and a skip would read as a pass.
            Assert.Fail($"The test subject would not launch: {launched.FailureMessage}");
            return;
        }

        _window = launched.Application.WindowHandle;

        UiSettle.UntilBoundsAreStable(_inspector, _window, Id("invokeOnly"));
    }

    [OneTimeTearDown]
    public void CloseTestApp() => AppLifetime.KillAll(TestApp.ProcessName);

    private string Id(string automationId) =>
        UiSettle.UntilSomethingMatches(_finder, _window, LocatorKind.AutomationId, automationId)[0];

    /// <summary>What the application says it last received.</summary>
    private string WhatTheApplicationSaw() =>
        _inspector.Attribute(_window, Id("lastPattern"), "Name").Value ?? "?";

    [Test]
    public void AButtonStillUsesInvoke()
    {
        // The control. A button maintains no state, so Invoke is genuinely
        // correct for it, and reordering the ladder must not have cost that.
        // Without this, "prefer Toggle and SelectionItem" and "never use Invoke"
        // predict the same result everywhere else in this fixture.
        _interactor.Click(_window, Id("invokeOnly")).Path.ShouldBe("Invoke");
    }

    [Test]
    public void WhenToggleAndInvokeAreBothOffered_TheApplicationReceivesToggle()
    {
        string elementId = Id("toggleAndInvoke");

        _inspector.Attribute(_window, elementId, "IsTogglePatternAvailable").Value
            .ShouldBe("True", "the subject must advertise both, or it tests nothing");
        _inspector.Attribute(_window, elementId, "IsInvokePatternAvailable").Value
            .ShouldBe("True", "the subject must advertise both, or it tests nothing");

        string before = _inspector.Attribute(_window, elementId, "Toggle.ToggleState").Value ?? "?";

        ElementAction click = _interactor.Click(_window, elementId);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);

        // The application's account, not the driver's.
        WhatTheApplicationSaw().ShouldBe("Toggle");

        // And the state moved, which Invoke on this control deliberately does
        // not do. Two independent observations of the same claim.
        _inspector.Attribute(_window, elementId, "Toggle.ToggleState").Value
            .ShouldNotBe(before);

        click.Path.ShouldBe("Toggle", "and the driver's own report agrees");
    }

    [Test]
    public void WhenSelectionItemAndInvokeAreBothOffered_TheApplicationReceivesSelectionItem()
    {
        string elementId = Id("selectionItemAndInvoke");

        _inspector.Attribute(_window, elementId, "IsSelectionItemPatternAvailable").Value
            .ShouldBe("True");
        _inspector.Attribute(_window, elementId, "IsInvokePatternAvailable").Value
            .ShouldBe("True");

        ElementAction click = _interactor.Click(_window, elementId);

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("SelectionItem");

        // Invoke on this control records itself and selects nothing, so the
        // selection is what separates the two.
        _inspector.IsSelected(_window, elementId).Value.ShouldBeTrue();
    }

    [Test]
    public void APatternlessChild_IsClickedThroughItsAncestor()
    {
        // The rung with field evidence behind it: a MAUI CollectionView whose
        // rows were bare labels. One level, deterministically.
        ElementAction click = _interactor.Click(_window, Id("patternlessChild"));

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("ancestor:1/Invoke");
    }

    [Test]
    [Ignore("Known gap, same root as AnEdit_IsClickedByFocusingIt: the WPF " +
            "provider refuses SetFocus even with the window foregrounded. " +
            "Measured — foregrounding works and SendInput works.")]
    public void SendKeys_TypesIntoTheElement_RatherThanReplacingItsValue()
    {
        // The distinction the protocol's element/value command actually needs.
        // SetValue replaces contents through ValuePattern; typing sends
        // keystrokes, so a second call APPENDS rather than overwriting. The
        // compatibility suite depends on that — it clears a field with
        // Control+A then Delete, which only typing can express.
        UiaElementInteractor typing = new(
            new CUIAutomationClass(),
            new UiaElementResolver(new CUIAutomationClass()),
            mouse: null,
            windows: new WindowLocator(),
            keyboard: new SendInputKeyboard());

        string edit = Id("edit");

        typing.SendKeys(_window, edit, "ab").Outcome.ShouldBe(ElementActionOutcome.Performed);
        typing.SendKeys(_window, edit, "cd").Outcome.ShouldBe(ElementActionOutcome.Performed);

        // Appended, not replaced. SetValue twice would leave "cd".
        _inspector.Attribute(_window, edit, "Value.Value").Value.ShouldBe("abcd");
    }

    [Test]
    public void SendKeys_WithoutAKeyboard_RefusesRatherThanFallingBackToSetValue()
    {
        // Replacing a field's contents is a different operation from typing into
        // it. Silently substituting one for the other would make Control+A then
        // Delete appear to work while doing something else entirely.
        UiaElementInteractor noKeyboard = new(
            new CUIAutomationClass(),
            new UiaElementResolver(new CUIAutomationClass()));

        noKeyboard.SendKeys(_window, Id("edit"), "x").Outcome
            .ShouldBe(ElementActionOutcome.NotInteractable);
    }

    [Test]
    public void APatternlessOrphan_IsClickedByTheMouse_WhenTheMouseRungIsWired()
    {
        // The last rung. This element has no pattern and no ancestor with one,
        // so every earlier rung declines and only real mouse input can reach it.
        // WinAppDriver clicks it; before this rung existed, this driver could
        // not — which is why the compatibility suite could not even CREATE a
        // stale element, since it does so by clicking a disabled button.
        UiaElementInteractor withMouse = new(
            new CUIAutomationClass(),
            new UiaElementResolver(new CUIAutomationClass()),
            new SendInputPointer(),
            new WindowLocator());

        ElementAction click = withMouse.Click(_window, Id("patternlessOrphan"));

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("mouse", "no pattern can carry this, so only the mouse can");
    }

    [Test]
    public void TheMouseRungIsSkippedEntirely_WithoutAWindowLocatorToGuardIt()
    {
        // An UNGUARDED coordinate click is worse than no click: it delivers
        // input to whatever application happens to be under the point. So the
        // rung is skipped rather than run unguarded, and the ladder refuses.
        UiaElementInteractor unguarded = new(
            new CUIAutomationClass(),
            new UiaElementResolver(new CUIAutomationClass()),
            new SendInputPointer(),
            windows: null);

        unguarded.Click(_window, Id("patternlessOrphan")).Outcome
            .ShouldBe(ElementActionOutcome.NotInteractable);
    }

    [Test]
    public void APatternlessOrphan_IsRefused()
    {
        // Nothing within three levels carries a pattern. Reporting success here
        // is the exact failure the ladder exists to avoid, and it is
        // indistinguishable from having worked unless the driver says so.
        _interactor.Click(_window, Id("patternlessOrphan")).Outcome
            .ShouldBe(ElementActionOutcome.NotInteractable);
    }

    [Test]
    public void AComboBox_IsClickedThroughExpandCollapse()
    {
        ElementAction click = _interactor.Click(_window, Id("expandCollapse"));

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("ExpandCollapse");
        _inspector.Attribute(_window, Id("expandCollapse"), "ExpandCollapse.ExpandCollapseState")
            .Value.ShouldBe("1", "ExpandCollapseState comes back as the raw UIA enum value");

        // Leave it closed, so a later test does not find a popup over the tree.
        _interactor.Click(_window, Id("expandCollapse"));
    }

    [Test]
    public void ADisabledElement_IsRefused_AndItsAncestorIsLeftAlone()
    {
        // Measured against Alarms & Clock on Windows 10, 2026-08-10.
        // AddAlarmButton was disabled; InvokePattern.Invoke() threw; the ladder
        // climbed one level to AlarmCollectionPageCommandBar — which advertises
        // Toggle and ExpandCollapse — toggled the app bar, and answered
        // status 0. Nine compatibility-suite tests fail from this, and every one
        // of them is told the click succeeded.
        string disabled = Id("disabledInsideToggle");
        string host = Id("toggleHostingDisabled");

        _inspector.Attribute(_window, disabled, "IsEnabled").Value
            .ShouldBe("False", "the subject must be disabled, or this tests nothing");

        string before = _inspector.Attribute(_window, host, "Toggle.ToggleState").Value ?? "?";

        ElementAction click = _interactor.Click(_window, disabled);

        click.Outcome.ShouldBe(ElementActionOutcome.NotInteractable);

        // The control, and the part that actually caught this. "Refused" and
        // "climbed and toggled the parent" both leave the disabled button
        // untouched, so only the bystander distinguishes them.
        _inspector.Attribute(_window, host, "Toggle.ToggleState").Value
            .ShouldBe(before, "the ancestor must not have been acted on instead");
    }

    [Test]
    [Ignore("Known gap: the driver does not foreground the target window, and " +
            "UIA refuses SetFocus against a background one. Measured, cause " +
            "understood, tracked in docs/LIMITATIONS.md.")]
    public void AnEdit_IsClickedByFocusingIt()
    {
        // Ignored, not deleted, and not weakened to match what the driver does.
        // Measured: focusable=True, enabled=True, offscreen=False, and SetFocus
        // still refused, because the window was not in the foreground. Being
        // focusable is not the same as being focusable right now. The fixture
        // cannot fix this either — Windows refuses SetForegroundWindow from a
        // process that is not already foreground, which is why the driver has to
        // do it. This test is the specification for that work.
        ElementAction click = _interactor.Click(_window, Id("edit"));

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("Focus");
        _inspector.Attribute(_window, Id("edit"), "HasKeyboardFocus").Value.ShouldBe("True");
    }
}

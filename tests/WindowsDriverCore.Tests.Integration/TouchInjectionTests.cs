using System;
using System.Collections.Generic;
using System.Threading;
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
/// Synthetic touch reaches a real application as touch.
/// </summary>
/// <remarks>
/// <para>
/// <b>The subject must distinguish touch from a mouse click, or this measures
/// nothing.</b> A WPF <c>Button</c> raises <c>Click</c> for either, so asserting
/// on <c>Click</c> would pass just as well if the implementation quietly sent a
/// mouse event through <c>SendInput</c> — and "asked for touch, received a
/// mouse" is exactly the lie this is built to avoid. So the subject reports
/// <c>TouchDown</c>, which a mouse cannot raise.
/// </para>
/// <para>
/// <b>Marked as synthesising real input.</b> It moves the actual pointer and
/// presses on whatever is under it, so it must not run while somebody is using
/// the machine — the same category as the click-ladder fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("SynthesisesRealInput")]
[NonParallelizable]
public sealed class TouchInjectionTests
{
    private const string TouchTarget = "touchTarget";

    private static SyntheticContact Contact(int x, int y, SyntheticContactPhase phase) =>
        new(SyntheticPointerKind.Touch, x, y, phase);

    [Test]
    public void TouchIsAvailable_OnAnySupportedWindows()
    {
        // Touch injection is Windows 8 and later, so it is inside this driver's
        // floor of Windows 10 1607. If this ever fails, the floor moved or the
        // API is being called wrongly - both worth failing loudly for.
        new SyntheticPointer().CanInject(SyntheticPointerKind.Touch).ShouldBeTrue(
            "InitializeTouchInjection is available from Windows 8 onward");
    }

    [Test]
    public void ATap_ArrivesAsTouch_NotAsAMouseClick()
    {
        string? app = TestApp.Path;
        if (app is null)
        {
            Assert.Ignore("The WPF test subject has not been built.");
            return;
        }

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(app, null, null));

        launched.Application.ShouldNotBeNull($"the subject would not launch: {launched.FailureMessage}");

        try
        {
            CUIAutomationClass automation = new();
            UiaElementFinder finder = new(automation, new UiaElementResolver(automation));
            UiaElementInspector inspector = new(automation, new UiaElementResolver(automation));

            nint window = launched.Application.WindowHandle;
            IReadOnlyList<string> ids = UiSettle.UntilSomethingMatches(
                finder, window, LocatorKind.AutomationId, TouchTarget);

            UiSettle.UntilBoundsAreStable(inspector, window, ids[0]);
            ElementRead<ElementBounds> bounds = inspector.ScreenBounds(window, ids[0]);
            bounds.Outcome.ShouldBe(ElementReadOutcome.Read);

            int x = bounds.Value.X + (bounds.Value.Width / 2);
            int y = bounds.Value.Y + (bounds.Value.Height / 2);

            new WindowLocator().BringToForeground(window);

            SyntheticPointer pointer = new();

            // DOWN, then UPDATES while in contact, then UP - the shape a real
            // digitiser produces. A bare down/up pair with nothing between is not
            // what the system expects to see, and the first version of this test
            // sent exactly that and was answered with a mouse event.
            pointer.Inject([Contact(x, y, SyntheticContactPhase.Down)])
                .ShouldBeTrue("the system must accept the contact-down frame");

            for (int frame = 0; frame < 5; frame++)
            {
                pointer.Inject([Contact(x, y, SyntheticContactPhase.Update)])
                    .ShouldBeTrue("and each update while the contact is held");
            }

            pointer.Inject([Contact(x, y, SyntheticContactPhase.Up)])
                .ShouldBeTrue("and the contact-up frame");

            // The subject writes what it saw into its own name. A mouse click
            // cannot produce this text, which is the whole point of the fixture.
            string Observed() =>
                inspector.Attribute(window, ids[0], "Name") is
                    { Outcome: ElementReadOutcome.Read, Value: string name }
                    ? name
                    : "(unreadable)";

            // The observed value is in the failure, not just the expectation.
            // "it did not say touch" and "it said mouse" are different defects -
            // the second means the injection landed as the WRONG KIND, which is
            // the substitution this whole fixture exists to catch.
            bool sawTouch = SpinWait.SpinUntil(
                () => Observed().Contains("TOUCH", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            sawTouch.ShouldBeTrue(
                $"the subject was asked for touch and reports: '{Observed()}'");
        }
        finally
        {
            AppLifetime.KillAll(TestApp.ProcessName);
        }
    }
}

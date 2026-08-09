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
/// Turning an element id back into a live element.
/// </summary>
/// <remarks>
/// Every element command a client sends carries an id and nothing else, so this
/// step sits in front of all of them. It is also where a stale element is
/// detected: the id simply stops resolving.
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class UiaElementResolverTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private UiaElementFinder _finder = null!;
    private UiaElementResolver _resolver = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        WindowLocator windows = new();
        ApplicationLauncher launcher = new(new MainWindowWaiter(TimeProvider.System), windows);
        CUIAutomationClass automation = new();
        _finder = new UiaElementFinder(automation, new UiaElementResolver(automation));
        _resolver = new UiaElementResolver(automation);

        LaunchResult launched = launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;

        // Wait for the tree to exist and stop moving. Without it,
        // Resolve_RoundTripsEveryIdTheFinderIssued intermittently failed with an
        // id that would not resolve moments after the finder issued it — the
        // application was still building its control tree between the two calls.
        UiSettle.UntilBoundsAreStable(
            new UiaElementInspector(automation, _resolver),
            _window,
            UiSettle.UntilSomethingMatches(
                _finder, _window, LocatorKind.AutomationId, "num5Button")[0]);
    }

    [OneTimeTearDown]
    public void CloseCalculator()
    {
        foreach (Process process in Process.GetProcessesByName("CalculatorApp"))
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

    [TestCase("num5Button", "Five")]
    [TestCase("num7Button", "Seven")]
    public void Resolve_ReturnsTheElementThatIdNames(string automationId, string expectedName)
    {
        // Two subjects rather than one, and the assertion is on the Name rather
        // than on "an element came back". A resolver that ignored the id and
        // returned the first descendant would satisfy "not null" for both, and
        // would satisfy "Name is Five" for one of them by luck. It cannot
        // satisfy both rows.
        IReadOnlyList<string> found = UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.AutomationId, automationId);

        using ElementLookupResult resolved = _resolver.Resolve(_window, found[0]);

        resolved.Outcome.ShouldBe(ElementLookupOutcome.Resolved);
        resolved.Element.ShouldNotBeNull();
        resolved.Element.CurrentName.ShouldBe(expectedName);
    }

    [Test]
    public void Resolve_RoundTripsEveryIdTheFinderIssued()
    {
        // The contract between the two types: an id this driver hands to a
        // client must come back. If the finder's formatting and the resolver's
        // comparison ever drift apart, every element command breaks at once
        // while find keeps working — a failure that would look like a UIA
        // problem rather than a formatting one.
        IReadOnlyList<string> buttons = UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.ControlType, "Button");
        buttons.Count.ShouldBeGreaterThan(5);

        foreach (string elementId in buttons)
        {
            using ElementLookupResult resolved = _resolver.Resolve(_window, elementId);

            resolved.Outcome.ShouldBe(ElementLookupOutcome.Resolved, $"id {elementId}");
        }
    }

    [TestCase("99999.99999.99999")]
    [TestCase("InvalidRuntimeId")]
    [TestCase("")]
    public void Resolve_AnIdThatIsNotInTheTree_IsNotFound(string elementId)
    {
        // Not an exception, and not a window failure. The route turns this into
        // status 10 or 7 depending on whether this server issued the id, which
        // is a question the resolver deliberately cannot answer.
        using ElementLookupResult resolved = _resolver.Resolve(_window, elementId);

        resolved.Outcome.ShouldBe(ElementLookupOutcome.NotFound);
        resolved.Element.ShouldBeNull();
    }

    [Test]
    public void Resolve_AgainstAHandleThatIsNotAWindow_ReportsNoSuchWindow()
    {
        using ElementLookupResult resolved = _resolver.Resolve(0xDEAD, "42.1.2.3");

        resolved.Outcome.ShouldBe(ElementLookupOutcome.NoSuchWindow);
    }
}

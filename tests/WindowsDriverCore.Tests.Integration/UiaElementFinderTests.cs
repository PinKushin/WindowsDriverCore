using System;
using System.Diagnostics;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The UI Automation finder against a real application.
///
/// Calculator is the target because its automation ids are stable and
/// documented by WinAppDriver's own samples, so a failure here is the driver
/// rather than a guess about the app.
/// </summary>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class UiaElementFinderTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private ApplicationLauncher _launcher = null!;
    private UiaElementFinder _finder = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        WindowLocator windows = new();
        _launcher = new ApplicationLauncher(new MainWindowWaiter(TimeProvider.System), windows);
        _finder = new UiaElementFinder(new CUIAutomationClass());

        LaunchResult launched = _launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;
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

    [Test]
    public void FindAll_ByAutomationId_FindsTheButton()
    {
        FindResult result = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");

        result.Failure.ShouldBe(FindFailure.None);
        result.ElementIds.ShouldNotBeEmpty();
    }

    [Test]
    public void FindAll_ByAutomationId_ThatDoesNotExist_MatchesNothing_WithoutFailing()
    {
        // The control for the test above. A finder that returned every element
        // regardless of the condition would pass it; this is the input where
        // correct and broken differ.
        FindResult result = _finder.FindAll(
            _window, LocatorKind.AutomationId, "ThisIdDoesNotExistAnywhere");

        result.Failure.ShouldBe(FindFailure.None);
        result.ElementIds.ShouldBeEmpty();
    }

    [Test]
    public void FindAll_ByLocalizedControlType_MatchesButtons()
    {
        // This is the locator the previous implementation got wrong by mapping
        // "tag name" to ControlType rather than LocalizedControlType. Matching
        // the wrong property does not error — it silently finds nothing.
        FindResult result = _finder.FindAll(_window, LocatorKind.LocalizedControlType, "button");

        result.Failure.ShouldBe(FindFailure.None);
        result.ElementIds.Count.ShouldBeGreaterThan(5, "Calculator has a keypad full of buttons");
    }

    [Test]
    public void FindAll_ByRuntimeId_RoundTripsAnIdThisDriverReturned()
    {
        // The element id format is only useful if it round-trips: a client takes
        // the id from a find and passes it back to FindElementById. This is the
        // test the compatibility suite's FindElement_ByRuntimeId performs.
        FindResult first = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");
        first.ElementIds.ShouldNotBeEmpty();

        string elementId = first.ElementIds[0];

        FindResult again = _finder.FindAll(_window, LocatorKind.RuntimeId, elementId);

        again.Failure.ShouldBe(FindFailure.None);
        again.ElementIds.ShouldContain(elementId);
    }

    [Test]
    public void ElementIds_AreDotSeparated_NotCommaSeparated()
    {
        // WinAppDriver emits "42.19466560.4.73". The previous implementation used
        // commas, which round-tripped within itself but did not match an id
        // copied from inspect.exe or from a WinAppDriver session.
        FindResult result = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");

        result.ElementIds[0].ShouldContain(".");
        result.ElementIds[0].ShouldNotContain(",");
    }

    [Test]
    public void FindAll_ByRuntimeId_WithAValueThatIsNotAnId_MatchesNothing()
    {
        // A client calling FindElementById with a name rather than a runtime id.
        // It must be a find miss, not an exception — WinAppDriver answers
        // "no such element" for this, which the route turns the empty result into.
        FindResult result = _finder.FindAll(_window, LocatorKind.RuntimeId, "InvalidRuntimeId");

        result.Failure.ShouldBe(FindFailure.None);
        result.ElementIds.ShouldBeEmpty();
    }

    [Test]
    public void FindAll_OnAClosedWindow_ReportsNoSuchWindow()
    {
        // A handle that is not a window at all. The finder must report this
        // rather than throwing a COM exception at the route.
        FindResult result = _finder.FindAll(0xDEAD, LocatorKind.AutomationId, "anything");

        result.Failure.ShouldBe(FindFailure.NoSuchWindow);
    }

    [Test]
    public void FindAll_RepeatedManyTimes_NeverReturnsEmptyForAnElementThatIsPresent()
    {
        // The project's founding hypothesis, in its weakest testable form:
        // WinAppDriver's #1079 is FindElements intermittently returning nothing
        // for an element that is present, because it searches a cached view.
        // This finder queries the live tree, so repetition must not change the
        // answer.
        //
        // Note what this does NOT establish. It shows the defect does not occur
        // here under repetition; it does not show WinAppDriver's does, because
        // there is no control running the same manipulation through WinAppDriver.
        // That comparison is the real experiment and is not built yet.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            FindResult result = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");

            result.Failure.ShouldBe(FindFailure.None);
            result.ElementIds.ShouldNotBeEmpty($"attempt {attempt} returned nothing");
        }
    }
}

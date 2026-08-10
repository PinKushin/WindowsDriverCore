using System;
using System.Diagnostics;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;

using WindowsDriverCore.Tests.Integration.Support;

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

    private UiaElementFinder _finder = null!;
    private nint _window;

    private static readonly CUIAutomationClass SharedAutomation = new();

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        _finder = new UiaElementFinder(SharedAutomation, new UiaElementResolver(SharedAutomation));

        // One Calculator for the whole run. See SharedCalculator.
        _window = SharedCalculator.Window();
        if (_window == 0)
        {
            Assert.Ignore("Calculator is not available.");
        }
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

    [TestCase("Button", true)]
    [TestCase("button", false)]
    [TestCase("Text", true)]
    [TestCase("text", false)]
    public void FindAll_ByControlType_MatchesTheProgrammaticName_CaseSensitively(
        string name, bool shouldMatch)
    {
        // Measured against WinAppDriver: "Button" 200, "button" 404, "ListItem"
        // 200, "list item" 404. It compares UIA_ControlTypePropertyId against the
        // enum's programmatic name.
        //
        // The case pairs are the whole point. Under the LocalizedControlType
        // reading this file used to hold, every prediction here inverts —
        // "button" matches and "Button" does not — so a wrong implementation
        // cannot pass by accident. An input of "Button" alone would prove
        // nothing, because a Button's localized type differs from its
        // programmatic name only by case; that is exactly how the wrong reading
        // survived review.
        FindResult result = _finder.FindAll(_window, LocatorKind.ControlType, name);

        result.Failure.ShouldBe(FindFailure.None);

        if (shouldMatch)
        {
            result.ElementIds.Count.ShouldBeGreaterThan(
                0, $"Calculator has elements of control type {name}");
        }
        else
        {
            result.ElementIds.ShouldBeEmpty($"'{name}' is not a control type name");
        }
    }

    [TestCase("InvalidTagName")]
    [TestCase("//@InvalidTagNameMalformed")]
    public void FindAll_ByControlType_WithAnUnknownName_MatchesNothing_WithoutFailing(string name)
    {
        // An unknown tag name must find nothing, so POST /element answers "no
        // such element". The previous implementation fell back to
        // UIA_CustomControlTypeId, which can silently succeed and return a real
        // element for a name that means nothing.
        FindResult result = _finder.FindAll(_window, LocatorKind.ControlType, name);

        result.Failure.ShouldBe(FindFailure.None);
        result.ElementIds.ShouldBeEmpty();
    }

    [TestCase(LocatorKind.AutomationId, "num5Button")]
    [TestCase(LocatorKind.ControlType, "Button")]
    [TestCase(LocatorKind.ControlType, "Text")]
    public void FindFirst_AgreesWithTheFirstResultOfFindAll(LocatorKind kind, string value)
    {
        // The claim that makes FindFirst safe to use for POST /element: UIA
        // returns matches in tree order for both calls, so "first" means the
        // same thing. That is a statement about UIA, not about this code, so it
        // is asserted against a real tree rather than assumed.
        //
        // ControlType cases matter more than the automation id: with one match
        // the two cannot disagree, so a single-match locator is an input where
        // correct and broken predict the same answer. Buttons and Texts have
        // many.
        FindResult all = _finder.FindAll(_window, kind, value);
        FindResult first = _finder.FindFirst(_window, kind, value);

        all.Failure.ShouldBe(FindFailure.None);
        first.Failure.ShouldBe(FindFailure.None);
        all.ElementIds.ShouldNotBeEmpty();

        first.ElementIds.Count.ShouldBe(1, "FindFirst returns at most one");
        first.ElementIds[0].ShouldBe(
            all.ElementIds[0],
            $"{kind} '{value}' matched {all.ElementIds.Count} elements and the two " +
            "calls disagree about which is first");
    }

    [Test]
    public void FindFirst_WithNoMatch_IsEmpty_NotAFailure()
    {
        FindResult result = _finder.FindFirst(_window, LocatorKind.AutomationId, "NoSuchThing");

        result.Failure.ShouldBe(FindFailure.None);
        result.ElementIds.ShouldBeEmpty();
    }

    [Test]
    public void FindFirst_ByRuntimeId_StillFindsTheRightElement()
    {
        // The one case that cannot stop early. UIA rejects RuntimeId in a
        // property condition, so a search by id runs a true condition and
        // compares — stopping at the first match would stop at the first element
        // in the tree, which is almost never the one asked for.
        FindResult found = _finder.FindAll(_window, LocatorKind.AutomationId, "num7Button");
        found.ElementIds.ShouldNotBeEmpty();

        string wanted = found.ElementIds[0];

        FindResult byId = _finder.FindFirst(_window, LocatorKind.RuntimeId, wanted);

        byId.Failure.ShouldBe(FindFailure.None);
        byId.ElementIds.ShouldBe([wanted]);
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
        for (int attempt = 0; attempt < 10; attempt++)
        {
            FindResult result = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");

            result.Failure.ShouldBe(FindFailure.None);
            result.ElementIds.ShouldNotBeEmpty($"attempt {attempt} returned nothing");
        }
    }
}

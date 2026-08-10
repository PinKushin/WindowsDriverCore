using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Searching inside an element rather than the whole window.
/// </summary>
/// <remarks>
/// <para>
/// This is the protocol's answer to duplicate and unnamed elements, and it is a
/// better one than inventing identifiers. Rows that are indistinguishable across
/// a window are usually unique within their own container — a MAUI
/// <c>CollectionView</c> whose children carry no automation id is reachable as
/// "the third ListItem inside this list" without the application changing.
/// </para>
/// <para>
/// The measurement that matters is <b>the negative one</b>: a locator that
/// succeeds at window scope must fail inside a container that does not hold it.
/// Without that, a "nested" find that quietly ignored its container would pass
/// every positive test here.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class NestedFindTests
{

    private UiaElementFinder _finder = null!;
    private nint _window;
    private string _keypad = null!;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        _finder = new UiaElementFinder(automation, resolver);

        // One Calculator for the whole run, opened THROUGH THE DRIVER.
        // See SharedDriverSession.
        _window = SharedDriverSession.Window();
        if (_window == 0)
        {
            Assert.Ignore("Calculator is not available.");
        }

        _keypad = UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.AutomationId, "NumberPad")[0];

        UiSettle.UntilBoundsAreStable(
            new UiaElementInspector(automation, resolver), _window, _keypad);
    }

    [Test]
    public void ANestedFind_FindsSomethingInsideTheContainer()
    {
        FindResult found = _finder.FindFirst(
            new SearchScope(_window, _keypad), LocatorKind.AutomationId, "num5Button");

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.ShouldNotBeEmpty("num5Button is inside the keypad");
    }

    [Test]
    public void ANestedFind_DoesNotFindSomethingOutsideTheContainer()
    {
        // The decisive case, and the control is the same locator at window scope.
        // Measured against WinAppDriver: CalculatorResults is found at window
        // scope and answers "no such element" inside the keypad.
        FindResult atWindowScope = _finder.FindFirst(
            _window, LocatorKind.AutomationId, "CalculatorResults");

        FindResult insideKeypad = _finder.FindFirst(
            new SearchScope(_window, _keypad), LocatorKind.AutomationId, "CalculatorResults");

        atWindowScope.ElementIds.ShouldNotBeEmpty(
            "the display exists, or this test proves nothing");
        insideKeypad.ElementIds.ShouldBeEmpty(
            "the display is not inside the keypad, so a scoped search must not find it");
    }

    [Test]
    public void ANestedFind_ReturnsFewerMatchesThanTheSameSearchOnTheWindow()
    {
        // Effect size: a container search must actually narrow. Buttons exist
        // both inside and outside the keypad, so the counts must differ — with a
        // locator matching the same set in both scopes, a broken implementation
        // would be indistinguishable.
        FindResult everywhere = _finder.FindAll(_window, LocatorKind.ControlType, "Button");
        FindResult inKeypad = _finder.FindAll(
            new SearchScope(_window, _keypad), LocatorKind.ControlType, "Button");

        inKeypad.ElementIds.ShouldNotBeEmpty();
        inKeypad.ElementIds.Count.ShouldBeLessThan(
            everywhere.ElementIds.Count,
            "the window has buttons outside the keypad, so scoping must reduce the count");
    }

    [Test]
    public void EveryNestedMatch_IsAlsoAWindowMatch()
    {
        // A container search must return a subset, never something new. A
        // resolver that fell back to the window on a container it could not use
        // would still pass the count test above on a different app.
        FindResult everywhere = _finder.FindAll(_window, LocatorKind.ControlType, "Button");
        FindResult inKeypad = _finder.FindAll(
            new SearchScope(_window, _keypad), LocatorKind.ControlType, "Button");

        foreach (string id in inKeypad.ElementIds)
        {
            everywhere.ElementIds.ShouldContain(id);
        }
    }

    [Test]
    public void ANestedFind_InsideAnUnknownContainer_MatchesNothing()
    {
        // Measured: WinAppDriver answers "no such element" for a nested find
        // against an element id it does not know, rather than falling back to
        // the window. Falling back is the dangerous failure — it would return a
        // real element for a container that does not exist.
        FindResult found = _finder.FindFirst(
            new SearchScope(_window, "99999.99999.99999"),
            LocatorKind.AutomationId,
            "num5Button");

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.ShouldBeEmpty();
    }
}

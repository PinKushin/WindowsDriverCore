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

    /// <summary>The container matches itself.</summary>
    /// <remarks>
    /// <para>
    /// <b>Measured from the compatibility suite, which asserts exactly this.</b>
    /// <c>FindNestedElements_ByAccessibilityId</c> searches the alarm tab for the
    /// alarm tab's own automation id and asserts the result is <b>one</b> element
    /// and that it IS the container. <c>FindNestedElements_ByRuntimeId</c> does
    /// the same with the container's runtime id. Both failed here with
    /// <c>Expected:&lt;1&gt;. Actual:&lt;0&gt;.</c>
    /// </para>
    /// <para>
    /// So a nested search is <c>TreeScope_Subtree</c> — the element AND its
    /// descendants — not <c>TreeScope_Descendants</c>. The distinction is
    /// invisible to every other nested test, because every other one looks for
    /// something genuinely below the container.
    /// </para>
    /// </remarks>
    [Test]
    public void ANestedFind_MatchesTheContainerItself_NotOnlyItsDescendants()
    {
        FindResult found = _finder.FindAll(
            new SearchScope(_window, _keypad), LocatorKind.AutomationId, "NumberPad");

        found.Failure.ShouldBe(FindFailure.None);

        // Exactly one, and it is the container. "At least one" would also pass
        // if the subtree scope somehow matched a nested NumberPad, and the suite
        // asserts the count rather than mere presence.
        found.ElementIds.Count.ShouldBe(1);
        found.ElementIds[0].ShouldBe(_keypad);
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

        // THE SIGNAL CHANGED, THE PROTOCOL ANSWER DID NOT. This used to report a
        // successful find of nothing, which the routes turned into "no such
        // element" - correct for an id nobody issued, and wrong for one this
        // server DID issue and which has since died, where the suite wants
        // "stale". This layer cannot tell those apart because it does not know
        // what was issued, so it now reports the container failure and the
        // protocol layer decides using the registry.
        found.Failure.ShouldBe(FindFailure.NoSuchContainer);

        // The guard this test exists for is untouched: no fallback to the window.
        // num5Button really is in that window, so a search that quietly restarted
        // at the root would answer with a live element here.
        found.ElementIds.ShouldBeEmpty();
    }
}

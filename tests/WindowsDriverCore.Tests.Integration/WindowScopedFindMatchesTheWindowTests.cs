using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A window-scoped find can match the window itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measurement <c>UiaElementFinder</c> said it was waiting for.</b> When
/// a nested find was widened to <c>TreeScope_Subtree</c>, the window-scoped case
/// was deliberately left on <c>TreeScope_Descendants</c> with the comment:
/// <i>"Nothing measured says a window-scoped find should match the window
/// element, and widening the scope every find runs under is not a change to make
/// on the strength of a nested measurement."</i> That was the right call and
/// this is the evidence it asked for.
/// </para>
/// <para>
/// <c>GetElementSize</c> in the compatibility suite does
/// <c>session.FindElementByClassName("ApplicationFrameWindow")</c> — the session
/// root's OWN class — and then asserts its size equals the window's.
/// WinAppDriver passes it; this driver answered zero, because
/// <c>TreeScope_Descendants</c> excludes the element you start from.
/// </para>
/// <para>
/// <b>The class name is read from the window rather than written down.</b>
/// Hardcoding <c>ApplicationFrameWindow</c> would pass on the Win10 guest and
/// silently skip on this host, where Calculator is a WinUI app with a different
/// class — a test that quietly stops testing is worse than one that fails.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class WindowScopedFindMatchesTheWindowTests
{
    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;
    private string _rootClassName = null!;
    private tagRECT _rootRectangle;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);

        _window = SharedDriverSession.Window();
        if (_window == 0)
        {
            Assert.Ignore("Calculator is not available.");
        }

        IUIAutomationElement root = automation.ElementFromHandle(_window);
        _rootClassName = root.CurrentClassName;
        _rootRectangle = root.CurrentBoundingRectangle;

        if (string.IsNullOrEmpty(_rootClassName))
        {
            Assert.Ignore("The session window reports no class name to search for.");
        }
    }

    /// <summary>Searching a window for its own class name finds the window.</summary>
    [Test]
    public void AWindowScopedFind_MatchesTheWindowItself()
    {
        FindResult found = _finder.FindFirst(_window, LocatorKind.ClassName, _rootClassName);

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.ShouldNotBeEmpty(
            $"the session window's own class is '{_rootClassName}', and the suite's " +
            "GetElementSize searches for exactly that at session scope");
    }

    /// <summary>And the element it returns is the window, not a namesake.</summary>
    /// <remarks>
    /// <para>
    /// A nested control sharing the root's class name would satisfy the test
    /// above without the scope having widened at all.
    /// </para>
    /// <para>
    /// <b>Identified by its rectangle, which is what the suite does too.</b>
    /// <c>GetElementSize</c> asserts the found element's size equals
    /// <c>session.Manage().Window.Size</c> — so matching on bounds is not a
    /// convenient proxy for the real assertion, it IS the real assertion.
    /// </para>
    /// </remarks>
    [Test]
    public void TheMatch_IsTheWindowElement_NotSomethingWithTheSameClass()
    {
        FindResult found = _finder.FindFirst(_window, LocatorKind.ClassName, _rootClassName);

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.ShouldNotBeEmpty();

        ElementRead<ElementBounds> bounds =
            _inspector.ScreenBounds(_window, found.ElementIds[0]);

        bounds.Outcome.ShouldBe(ElementReadOutcome.Read);
        bounds.Value.Width.ShouldBe(_rootRectangle.right - _rootRectangle.left);
        bounds.Value.Height.ShouldBe(_rootRectangle.bottom - _rootRectangle.top);
    }

    /// <summary>
    /// An ordinary descendant find is unaffected.
    /// </summary>
    /// <remarks>
    /// <b>The control, and it covers the whole driver rather than this case.</b>
    /// Every find in the product runs at window scope, so widening it is not a
    /// local change — if the root started intercepting matches, or the scope
    /// change broke enumeration, this is where it would show. The rest of the
    /// suite is the real control; this is the one that names the risk.
    /// </remarks>
    [Test]
    public void ADescendantFind_StillFindsTheDescendant()
    {
        FindResult found = _finder.FindFirst(_window, LocatorKind.AutomationId, "num5Button");

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.ShouldNotBeEmpty("num5Button is inside Calculator");

        // A descendant search must not start answering with the root. A digit
        // key is far smaller than the window, so equal bounds would mean the
        // root had intercepted the match.
        ElementRead<ElementBounds> bounds =
            _inspector.ScreenBounds(_window, found.ElementIds[0]);

        bounds.Outcome.ShouldBe(ElementReadOutcome.Read);
        bounds.Value.Width.ShouldBeLessThan(_rootRectangle.right - _rootRectangle.left);
    }

    /// <summary>Something absent is still absent.</summary>
    /// <remarks>
    /// The negative case. A scope change that accidentally searched from the
    /// desktop rather than the window would satisfy every test above and start
    /// matching other applications' controls.
    /// </remarks>
    [Test]
    public void SomethingThatIsNotThere_IsStillNotFound()
    {
        FindResult found = _finder.FindFirst(
            _window, LocatorKind.AutomationId, "NoSuchControlExistsAnywhere");

        found.ElementIds.ShouldBeEmpty();
    }
}

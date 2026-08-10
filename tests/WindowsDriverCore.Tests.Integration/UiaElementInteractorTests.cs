using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The click ladder against a real application.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every assertion here reads application state, never the driver's report of
/// success.</b> That is the whole point: the failure this ladder exists to fix
/// was a click that dispatched, took a second, returned success, and ran no
/// handler. A test that asserts on the return value cannot tell those apart, and
/// would have passed against the implementation being replaced.
/// </para>
/// <para>
/// Clicks here are counted in single digits. Repetition buys nothing — a click
/// either runs the handler or it does not, and that is visible the first time.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class UiaElementInteractorTests
{

    private CUIAutomationClass _automation = null!;
    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private UiaElementInteractor _interactor = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        _automation = new CUIAutomationClass();
        _finder = new UiaElementFinder(_automation, new UiaElementResolver(_automation));

        UiaElementResolver resolver = new(_automation);
        _inspector = new UiaElementInspector(_automation, resolver);
        _interactor = new UiaElementInteractor(_automation, resolver);

        // One Calculator for the whole run. See SharedCalculator.
        _window = SharedCalculator.Window();
        if (_window == 0)
        {
            Assert.Ignore("Calculator is not available.");
        }
        UiSettle.UntilBoundsAreStable(_inspector, _window, Find("num5Button"));
    }

    /// <summary>Waits for a control and returns its id.</summary>
    /// <remarks>
    /// Waits rather than asserts. A launched application has a window before it
    /// has a full control tree, and asserting immediately turns that gap into an
    /// intermittent failure that reads as a driver defect — seen as
    /// "clearButton must exist" and as an id that would not resolve moments
    /// after being issued.
    /// </remarks>
    private string Find(string automationId) =>
        UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.AutomationId, automationId)[0];

    private string DisplayText() => _inspector.Text(_window, Find("CalculatorResults")).Value ?? string.Empty;

    [Test]
    public void ClickingAButton_RunsItsHandler()
    {
        // The measurement is Calculator's display, not the return value. A driver
        // that dispatched input into empty space and reported success passes an
        // assertion on the return value and fails this one.
        _interactor.Click(_window, Find("clearButton")).Outcome
            .ShouldBe(ElementActionOutcome.Performed);

        string before = DisplayText();

        ElementAction click = _interactor.Click(_window, Find("num7Button"));

        click.Outcome.ShouldBe(ElementActionOutcome.Performed);
        click.Path.ShouldBe("Invoke", "a XAML button carries InvokePattern");

        DisplayText().ShouldNotBe(before, "the display must change, or no handler ran");
        DisplayText().ShouldContain("7");
    }

    [Test]
    public void ClickingUsesThePatternPath_NotACoordinate()
    {
        // The distinction the project exists to make. A coordinate click would
        // also work on this button, so the observation that separates them is
        // which mechanism ran — which is why ElementAction carries the path.
        _interactor.Click(_window, Find("num5Button")).Path.ShouldBe("Invoke");
    }

    [Test]
    public void ClearingAnElementWithNothingToClear_Succeeds()
    {
        // Measured against WinAppDriver: /clear on a Calculator button answers
        // 200. Doing nothing and reporting success is the contract here, which is
        // rare enough in this driver to be worth a test of its own.
        _interactor.Clear(_window, Find("num5Button")).Outcome
            .ShouldBe(ElementActionOutcome.Performed);
    }

    [Test]
    public void SettingAValueOnAnElementThatCannotTakeOne_IsNotInteractable_NotSilentSuccess()
    {
        // The failure mode the ladder's last rung exists to prevent. A button has
        // no ValuePattern, so there is nothing to set, and saying "done" would be
        // indistinguishable from having worked.
        ElementAction action = _interactor.SetValue(_window, Find("num5Button"), "hello");

        action.Outcome.ShouldBe(ElementActionOutcome.NotInteractable);
    }

    [Test]
    public void ActingOnAnIdThatIsNotInTheTree_IsNotFound()
    {
        _interactor.Click(_window, "99999.99999.99999").Outcome
            .ShouldBe(ElementActionOutcome.NotFound);
    }

    [Test]
    public void ActingAgainstAHandleThatIsNotAWindow_IsNoSuchWindow()
    {
        _interactor.Click(0xDEAD, "42.1.2.3").Outcome
            .ShouldBe(ElementActionOutcome.NoSuchWindow);
    }
}

using Interop.UIAutomationClient;
using NSubstitute;
using WindowsDriverCore.Platform.Windows;
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

        // One Calculator for the whole run, opened THROUGH THE DRIVER.
        // See SharedDriverSession.
        _window = SharedDriverSession.Window();
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

    /// <summary>
    /// Typing at an element with no value is ACCEPTED, matching the recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This asserted the opposite until the recording was read.</b> The code
    /// refused an element with no ValuePattern and the comment justified it as
    /// "the recorded contract answers 400 ElementNotInteractable". No such record
    /// exists. What the recording holds is
    /// <c>error.element.sendKeysDisabled.ClearMemoryButton</c> — WinAppDriver
    /// sent <c>{"value":["x"]}</c> to a DISABLED Calculator button and answered
    /// <c>200 status 0</c>.
    /// </para>
    /// <para>
    /// <b>The refusal was not free.</b> <c>SendKeys_ModifierWindowsKey</c>
    /// dismisses the Action Center it opened by sending Escape TO THE PANE, which
    /// has no ValuePattern. Refusing left the pane on screen holding the
    /// foreground, and every later test in that class failed with "could not be
    /// brought to the foreground".
    /// </para>
    /// <para>
    /// The keyboard is substituted so this measures the DECISION rather than
    /// typing into whatever is in front of the developer.
    /// </para>
    /// </remarks>
    [Test]
    public void TypingAtAnElementWithNoValue_IsAccepted_AsWinAppDriverDoes()
    {
        IKeyboardInput keyboard = Substitute.For<IKeyboardInput>();
        keyboard.Type(Arg.Any<string>()).Returns(true);

        UiaElementInteractor typing = new(
            _automation,
            new UiaElementResolver(_automation),
            windows: new WindowLocator(),
            keyboard: keyboard);

        ElementAction action = typing.SendKeys(_window, Find("num5Button"), "hello");

        action.Outcome.ShouldBe(
            ElementActionOutcome.Performed,
            "the reference driver types at a button rather than refusing it");

        keyboard.Received(1).Type("hello");
    }

    /// <summary>A failed raise is reported, not swallowed.</summary>
    /// <remarks>
    /// <para>
    /// <b>Synthesised keys go to whatever holds the FOREGROUND, not to a
    /// handle.</b> So a raise that did not happen means the keystrokes landed in
    /// another window — and this still answers <c>Performed</c>, because refusing
    /// to type deadlocks a caller trying to dismiss a shell surface it just
    /// opened. The outcome is therefore useless as a signal, and the path is the
    /// only place the fact can live.
    /// </para>
    /// <para>
    /// <b>Why it matters beyond tidiness.</b> The result was discarded, exactly
    /// as the input drain's was, and that made a whole class of failure
    /// invisible: `focused=48, unfocused=0` in a guest run measures
    /// <c>SetFocus</c>, a different call, and the `keys -&gt; raised` counter
    /// belongs to the session-level route — the family that never fails. Nothing
    /// measured the raise on the element path, which is the family that flaps.
    /// </para>
    /// </remarks>
    [Test]
    public void TypingWhenTheWindowWouldNotComeForward_SaysSoInThePath()
    {
        IKeyboardInput keyboard = Substitute.For<IKeyboardInput>();
        keyboard.Type(Arg.Any<string>()).Returns(true);

        IWindowLocator refusing = Substitute.For<IWindowLocator>();
        refusing.BringToForeground(Arg.Any<nint>()).Returns(false);

        UiaElementInteractor typing = new(
            _automation,
            new UiaElementResolver(_automation),
            windows: refusing,
            keyboard: keyboard);

        ElementAction action = typing.SendKeys(_window, Find("num5Button"), "hello");

        // Still Performed. That is the measured reference behaviour and it is why
        // the path has to carry the warning instead.
        action.Outcome.ShouldBe(ElementActionOutcome.Performed);
        action.Path.ShouldStartWith("keys (NOT RAISED");
    }

    /// <summary>The control: a raise that works is not labelled as a failure.</summary>
    /// <remarks>
    /// Without this, a path hard-coded to the warning would pass the test above.
    /// The two together are what make the label mean anything.
    /// </remarks>
    [Test]
    public void TypingWhenTheWindowDoesComeForward_IsNotLabelledAsAFailedRaise()
    {
        IKeyboardInput keyboard = Substitute.For<IKeyboardInput>();
        keyboard.Type(Arg.Any<string>()).Returns(true);

        IWindowLocator raising = Substitute.For<IWindowLocator>();
        raising.BringToForeground(Arg.Any<nint>()).Returns(true);

        UiaElementInteractor typing = new(
            _automation,
            new UiaElementResolver(_automation),
            windows: raising,
            keyboard: keyboard);

        ElementAction action = typing.SendKeys(_window, Find("num5Button"), "hello");

        action.Path.ShouldNotContain("NOT RAISED");
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

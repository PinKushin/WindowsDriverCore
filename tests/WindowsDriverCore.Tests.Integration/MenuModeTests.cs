using System;
using System.Runtime.InteropServices;
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
/// A click while a modal menu is open.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measured defect.</b> The compatibility suite's <c>MouseClick</c>
/// right-clicks Calculator's title bar to raise the system menu, asserts it
/// contains Minimize, and then dismisses it — the suite's own comment on the
/// line is <c>// Dismiss the context menu</c>. WinAppDriver serves an element
/// click as a physical click, which closes a menu as a side effect. This driver
/// prefers <c>InvokePattern</c>, which sends no input and therefore closes
/// nothing, so the menu survives and the next two tests in the class both act on
/// the title bar underneath it — <c>MouseDoubleClick</c> and
/// <c>MouseDownMoveUp</c>, both failing on the guest at <c>93e3fd7</c>.
/// </para>
/// <code>
/// dismissed by             menu open   then maximized   (guest, 2 rounds each)
/// element click (Invoke)   YES         no
/// moveto + click (REAL)    no          YES
/// </code>
/// <para>
/// <b>Menu mode is entered by a posted message, not by synthesised input.</b>
/// <c>WM_SYSCOMMAND</c>/<c>SC_KEYMENU</c> is what Alt+Space sends, and posting
/// it makes the subject's own message loop enter the modal menu loop — so this
/// never touches the mouse or the keyboard and cannot type into whatever happens
/// to be in front.
/// </para>
/// <para>
/// <b>The WPF subject, and the first attempt used the Win32 one and proved
/// nothing.</b> Against a Win32 <c>BUTTON</c> all four tests passed BEFORE the
/// rung existed: UI Automation reaches a legacy control through the MSAA bridge,
/// which sends the application a real window message, and that ends the menu
/// loop by itself. Correct and broken predicted the same observation. A WPF
/// button's <c>Invoke</c> runs its handler on the dispatcher and sends the menu
/// loop nothing, which is the condition the defect actually lives in.
/// </para>
/// <para>
/// Integration rather than unit because the state being read belongs to the
/// desktop. These must run under the machine-wide lock.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class MenuModeTests
{
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_CANCELMODE = 0x001F;
    private const nint SC_KEYMENU = 0xF100;

    private WindowLocator _windows = null!;
    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private UiaElementInteractor _interactor = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchTheSubject()
    {
        string? path = TestApp.Path;
        if (path is null)
        {
            Assert.Ignore("The WPF test subject has not been built.");
        }

        _windows = new WindowLocator();

        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);

        // With a real pointer, or the mouse rung cannot run and "the ladder
        // refused" and "the rung was unreachable" become the same observation.
        _interactor = new UiaElementInteractor(
            automation, resolver, new SendInputPointer(), _windows);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), _windows)
            .Launch(new ApplicationTarget(path, null, null));

        if (launched.Application is null)
        {
            // Fail rather than ignore: this application is built by this
            // solution, so it not launching is a defect here and a skip would
            // read as a pass.
            Assert.Fail($"The test subject would not launch: {launched.FailureMessage}");
            return;
        }

        _window = launched.Application.WindowHandle;
    }

    [TearDown]
    public void LeaveNoMenuBehind()
    {
        // A subject abandoned inside a modal menu loop would wedge every test
        // after it, and on a shared desktop that is somebody else's problem too.
        // WM_CANCELMODE is what Windows itself sends to end a modal loop.
        if (_window != 0)
        {
            MenuProbe.PostMessage(_window, WM_CANCELMODE, 0, 0);
            SpinWait.SpinUntil(() => !_windows.IsMenuModeActive(), 2000);
        }
    }

    [OneTimeTearDown]
    public void CloseTheSubject()
    {
        if (_window != 0)
        {
            _windows.Close(_window);
        }
    }

    /// <summary>An open system menu is reported as menu mode.</summary>
    /// <remarks>
    /// <b>The manipulation.</b> Without this the whole rung is dead code that
    /// reports "no menu" forever, and a detector stuck on false is
    /// indistinguishable from one that works on a desktop with no menu open —
    /// which is every ordinary moment.
    /// </remarks>
    [Test]
    public void AnOpenSystemMenu_IsMenuMode()
    {
        OpenTheSystemMenu();

        _windows.IsMenuModeActive().ShouldBeTrue("the system menu is open on the foreground window");
    }

    /// <summary>An ordinary foreground window is not menu mode.</summary>
    /// <remarks>
    /// <b>The control, and the half that keeps the detector honest.</b> One that
    /// answered true unconditionally would pass the test above and route every
    /// single click through the mouse — silently discarding the pattern ladder
    /// this project exists for.
    /// </remarks>
    [Test]
    public void AnOrdinaryWindow_IsNotMenuMode()
    {
        _windows.BringToForeground(_window).ShouldBeTrue("the subject must be in front");

        _windows.IsMenuModeActive().ShouldBeFalse("no menu has been opened");
    }

    /// <summary>A click while a menu is open ends the menu.</summary>
    /// <remarks>
    /// <para>
    /// <b>The defect itself, measured on the desktop rather than on the driver's
    /// own account of what it did.</b> A path string is the driver marking its
    /// own homework; <c>IsMenuModeActive</c> reads the state that broke the two
    /// compatibility tests. An <c>Invoke</c> leaves it true.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT asserted: that the button was pressed.</b> A
    /// real click outside an open menu is swallowed by the dismissal — Windows'
    /// behaviour, not WinAppDriver's, and the reference is subject to it too.
    /// Asserting either way would be asserting something this driver does not
    /// control.
    /// </para>
    /// </remarks>
    [Test]
    public void AClickWhileAMenuIsOpen_EndsTheMenu()
    {
        string button = Id("invokeOnly");

        OpenTheSystemMenu();

        _interactor.Click(_window, button)
            .Outcome.ShouldBe(ElementActionOutcome.Performed);

        SpinWait.SpinUntil(() => !_windows.IsMenuModeActive(), 2000);

        _windows.IsMenuModeActive()
            .ShouldBeFalse("a click must dismiss an open menu, as a real one does");
    }

    /// <summary>When the mouse cannot deliver, the ladder still runs.</summary>
    /// <remarks>
    /// <para>
    /// <b>The rung must be an addition, never a subtraction.</b> Real mouse
    /// input can refuse for reasons that have nothing to do with the menu — no
    /// pointer configured, a zero rectangle, or the guard finding that another
    /// window owns the point. Returning that refusal would mean a menu being
    /// open anywhere on the desktop disables the pattern ladder entirely.
    /// </para>
    /// <para>
    /// <b>The case this is really about.</b> The suite's own
    /// <c>DeletePreviouslyCreatedAlarmEntry</c> right-clicks an alarm and then
    /// clicks <c>Delete</c> INSIDE the popup it raised. That popup carries its
    /// own window handle, so <c>OwnsThePointAt</c> against the session's window
    /// says no — and a rung that refused there would break the helper that nine
    /// add-alarm tests depend on.
    /// </para>
    /// <para>
    /// The interactor here is built without a pointer, which makes the mouse
    /// rung unreachable by construction. That is the cheapest honest way to
    /// reach the refusal path: no desktop state has to be arranged for it, and
    /// it cannot pass by accident.
    /// </para>
    /// </remarks>
    [Test]
    public void WhenTheMouseCannotDeliver_TheLadderStillRuns()
    {
        string button = Id("invokeOnly");

        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        UiaElementInteractor withoutAPointer = new(automation, resolver, null, _windows);

        OpenTheSystemMenu();

        ElementAction clicked = withoutAPointer.Click(_window, button);

        clicked.Outcome.ShouldBe(
            ElementActionOutcome.Performed,
            "an unreachable mouse rung must fall through, not refuse");
        clicked.Path.ShouldBe("Invoke");
    }

    /// <summary>With no menu open, the pattern ladder is unchanged.</summary>
    /// <remarks>
    /// <para>
    /// <b>The control that matters most.</b> A rung that fired unconditionally
    /// would fix the two compatibility tests and quietly turn every element click
    /// into a coordinate click — precisely the behaviour
    /// <c>docs/CLICK-SEMANTICS.md</c> exists to avoid, and the capability this
    /// project claims over the reference.
    /// </para>
    /// <para>
    /// <b>Asserted on the path alone, and the first draft asserted more and was
    /// wrong.</b> It also required the application's own <c>lastPattern</c> label
    /// to read "Invoke" — but <c>invokeOnly</c> is a plain <c>Button</c> with no
    /// handler, so it reports "none" whatever reaches it. The cross-boundary
    /// check belongs to the dual-pattern controls, which
    /// <c>LadderAgainstOwnSubjectTests</c> already uses it for; here it only made
    /// the test fail for a reason that was not the subject.
    /// </para>
    /// </remarks>
    [Test]
    public void AClickWithNoMenuOpen_StillGoesThroughThePattern()
    {
        _windows.BringToForeground(_window).ShouldBeTrue("the subject must be in front");
        _windows.IsMenuModeActive().ShouldBeFalse("no menu may be open for this control");

        string button = Id("invokeOnly");
        UiSettle.UntilBoundsAreStable(_inspector, _window, button);

        _interactor.Click(_window, button).Path.ShouldBe("Invoke");
    }

    /// <summary>Puts the subject into menu mode and waits for it.</summary>
    private void OpenTheSystemMenu()
    {
        _windows.BringToForeground(_window).ShouldBeTrue("the subject must be in front to own menu mode");

        MenuProbe.PostMessage(_window, WM_SYSCOMMAND, SC_KEYMENU, ' ');

        SpinWait.SpinUntil(() => _windows.IsMenuModeActive(), 3000);

        _windows.IsMenuModeActive().ShouldBeTrue("the menu must be open, or the test measures nothing");
    }

    private string Id(string automationId) =>
        UiSettle.UntilSomethingMatches(_finder, _window, LocatorKind.AutomationId, automationId)[0];
}

/// <summary>Posting a message to put a window into menu mode.</summary>
/// <remarks>
/// <b>Posted, never sent.</b> <c>SendMessage</c> would block this thread inside
/// the subject's modal menu loop until something dismissed it, which nothing
/// would. Declared here rather than in <c>Win32</c> because the driver has no
/// reason to open a menu — only to notice one.
/// </remarks>
internal static class MenuProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}

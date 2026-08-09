using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
/// Element properties read from a real application.
/// </summary>
/// <remarks>
/// Calculator answers most of these. The one thing it cannot settle is whether
/// <c>/text</c> prefers a ValuePattern over Name, because nothing in it has
/// both — that condition was measured against Settings and is covered by the
/// protocol tests plus <c>docs/PROJECT-KNOWLEDGE.md</c>.
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class UiaElementInspectorTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out WindowRect rect);

    /// <summary>
    /// Win32 <c>RECT</c>, as <c>GetWindowRect</c> fills it.
    /// </summary>
    /// <remarks>
    /// All four fields are written by the P/Invoke, which the analyser cannot
    /// see; <c>Right</c> and <c>Bottom</c> are unread here but must be declared
    /// or the struct is the wrong size and the call writes past it.
    /// </remarks>
    [SuppressMessage("Minor Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "Layout must match Win32 RECT even where fields are unread.")]
    [SuppressMessage("Major Code Smell", "S3459:Unassigned members should be removed",
        Justification = "GetWindowRect assigns every field through the P/Invoke.")]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        WindowLocator windows = new();
        ApplicationLauncher launcher = new(new MainWindowWaiter(TimeProvider.System), windows);
        CUIAutomationClass automation = new();
        _finder = new UiaElementFinder(automation);
        _inspector = new UiaElementInspector(automation, new UiaElementResolver(automation));

        LaunchResult launched = launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;

        // Same reason as ElementAttributeTests: the geometry assertions compare
        // readings taken at different moments, which is only valid once the
        // window has stopped arriving.
        UiSettle.UntilBoundsAreStable(_inspector, _window, Find("num5Button"));
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

    private void MoveWindowTo(int x, int y) =>
        SetWindowPos(_window, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate)
            .ShouldBeTrue("the window must actually move or this measures nothing");

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

    [TestCase("num5Button", "ControlType.Button")]
    [TestCase("CalculatorResults", "ControlType.Text")]
    public void TagName_IsThePrefixedControlTypeName(string automationId, string expected)
    {
        // Two control types, so a hard-coded "ControlType.Button" cannot pass.
        ElementRead<string> read = _inspector.TagName(_window, Find(automationId));

        read.Outcome.ShouldBe(ElementReadOutcome.Read);
        read.Value.ShouldBe(expected);
    }

    [TestCase("num5Button", "Five")]
    [TestCase("num7Button", "Seven")]
    public void Text_OfAnElementWithNoValuePattern_IsItsName(string automationId, string expected)
    {
        ElementRead<string> read = _inspector.Text(_window, Find(automationId));

        read.Outcome.ShouldBe(ElementReadOutcome.Read);
        read.Value.ShouldBe(expected);
    }

    [Test]
    public void IsEnabled_AndIsDisplayed_AreTrueForAVisibleButton()
    {
        string element = Find("num5Button");

        _inspector.IsEnabled(_window, element).Value.ShouldBeTrue();
        _inspector.IsDisplayed(_window, element).Value.ShouldBeTrue();
    }

    [Test]
    public void IsSelected_OfAnElementWithNoSelectionItemPattern_IsFalse_NotAnError()
    {
        // Asserted by WinAppDriver's GetElementSelectedState_UnselectableElement.
        // An implementation that threw when the pattern is missing would fail
        // every Selected check on an ordinary button.
        ElementRead<bool> read = _inspector.IsSelected(_window, Find("num5Button"));

        read.Outcome.ShouldBe(ElementReadOutcome.Read);
        read.Value.ShouldBeFalse();
    }

    [Test]
    public void WindowRelativeBounds_DoNotMoveWhenTheWindowDoes()
    {
        // The experiment for the coordinate space, and the reason it is done by
        // moving the window rather than by comparing against GetWindowRect.
        //
        // Manipulation: move the window. Prediction: screen bounds shift by
        // exactly the same amount, window-relative bounds do not shift at all.
        // An implementation that returned screen coordinates from /location
        // would move both — which is invisible on a window at the top-left of
        // the primary display, and is exactly the mistake the recordings caught.
        string element = Find("num5Button");

        // Park the window at a known place first, so the displacement below is a
        // difference between two SetWindowPos calls rather than between a
        // SetWindowPos argument and a GetWindowRect reading.
        //
        // Those two disagree. Measured on this machine: setting x to
        // GetWindowRect's Left + 120 produced a reported Left 175 greater — a
        // constant 55px offset, because GetWindowRect reports the frame
        // including the invisible resize border while SetWindowPos positions
        // something else. Taking a delta between two identical operations
        // cancels the offset; predicting an absolute value does not.
        MoveWindowTo(240, 160);
        UiSettle.UntilBoundsAreStable(_inspector, _window, element);

        GetWindowRect(_window, out WindowRect windowBefore).ShouldBeTrue();
        ElementBounds screenBefore = _inspector.ScreenBounds(_window, element).Value;
        ElementBounds relativeBefore = _inspector.WindowRelativeBounds(_window, element).Value;

        MoveWindowTo(240 + 120, 160 + 80);

        ElementBounds screenAfter = _inspector.ScreenBounds(_window, element).Value;
        ElementBounds relativeAfter = _inspector.WindowRelativeBounds(_window, element).Value;

        // Two checks, and deliberately in two coordinate systems that are never
        // mixed.
        //
        // Interference, in Win32 space: did the window end up where SetWindowPos
        // was told to put it? This test shares a desktop with whoever is using
        // the machine, and a window dragged mid-test would otherwise fail below
        // with a message about coordinate spaces that says nothing about the
        // real cause. See docs/LIMITATIONS.md — the answer is diagnosability,
        // never a retry.
        GetWindowRect(_window, out WindowRect windowAfter).ShouldBeTrue();
        (windowAfter.Left - windowBefore.Left).ShouldBe(
            120,
            "the window did not move as instructed — something else moved it, " +
            "possibly a person using the machine");
        (windowAfter.Top - windowBefore.Top).ShouldBe(80, "same, vertically");


        // The claim under test, in UIA space only. An earlier version compared
        // the Win32 displacement against the UIA one and expected them equal;
        // they were 120 and 175 on this machine. Mixing GetWindowRect with UIA
        // bounding rectangles is exactly the coordinate mismatch the production
        // code avoids by taking both rectangles from UIA, and the test had
        // reintroduced it.
        int movedBy = screenAfter.X - screenBefore.X;
        movedBy.ShouldNotBe(0, "the manipulation must have an effect to be a manipulation");

        // The bystander: the element did not move within its window.
        relativeAfter.X.ShouldBe(relativeBefore.X);
        relativeAfter.Y.ShouldBe(relativeBefore.Y);

        // Size is a difference, so it survives the move in either space.
        relativeAfter.Width.ShouldBe(screenBefore.Width);
        relativeAfter.Height.ShouldBe(screenBefore.Height);
    }

    [Test]
    public void OffsetBetweenTheTwoSpaces_IsOneConstantPerWindow_AndIsNotZero()
    {
        // A second, independent statement of the same rule: the difference
        // between the two spaces is one constant per window, not per element.
        //
        // The non-zero assertion is not decoration. Without it this test passes
        // when the subtraction is removed entirely — both offsets are then 0 and
        // 0 equals 0 — which is what a mutation run showed. Equality alone is
        // insensitive to the manipulation it exists to detect, so the window is
        // moved somewhere unambiguous first.
        SetWindowPos(_window, 0, 137, 91, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate)
            .ShouldBeTrue();

        string five = Find("num5Button");
        string seven = Find("num7Button");

        int offsetOfFive =
            _inspector.ScreenBounds(_window, five).Value.X -
            _inspector.WindowRelativeBounds(_window, five).Value.X;

        int offsetOfSeven =
            _inspector.ScreenBounds(_window, seven).Value.X -
            _inspector.WindowRelativeBounds(_window, seven).Value.X;

        offsetOfFive.ShouldNotBe(0, "a window at x=137 cannot have a zero offset");
        offsetOfFive.ShouldBe(offsetOfSeven);
    }

    [Test]
    public void ReadingAnIdThatIsNotInTheTree_IsNotFound()
    {
        _inspector.Text(_window, "99999.99999.99999").Outcome
            .ShouldBe(ElementReadOutcome.NotFound);
    }

    [Test]
    public void ReadingAgainstAHandleThatIsNotAWindow_IsNoSuchWindow()
    {
        _inspector.Text(0xDEAD, "42.1.2.3").Outcome
            .ShouldBe(ElementReadOutcome.NoSuchWindow);
    }
}

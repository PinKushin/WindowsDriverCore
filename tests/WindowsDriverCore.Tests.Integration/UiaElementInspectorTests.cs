using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    private string Find(string automationId)
    {
        FindResult found = _finder.FindAll(_window, LocatorKind.AutomationId, automationId);
        found.ElementIds.ShouldNotBeEmpty($"{automationId} must exist for this test to mean anything");

        return found.ElementIds[0];
    }

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

        ElementBounds screenBefore = _inspector.ScreenBounds(_window, element).Value;
        ElementBounds relativeBefore = _inspector.WindowRelativeBounds(_window, element).Value;

        SetWindowPos(
            _window, 0, screenBefore.X + 120, screenBefore.Y + 80, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate)
            .ShouldBeTrue("the window must actually move or this measures nothing");

        ElementBounds screenAfter = _inspector.ScreenBounds(_window, element).Value;
        ElementBounds relativeAfter = _inspector.WindowRelativeBounds(_window, element).Value;

        int movedBy = screenAfter.X - screenBefore.X;
        movedBy.ShouldNotBe(0, "the manipulation must have an effect to be a manipulation");

        // The bystander: the element did not move within its window.
        relativeAfter.X.ShouldBe(relativeBefore.X);
        relativeAfter.Y.ShouldBe(relativeBefore.Y);

        // And the subject did.
        (screenAfter.Y - screenBefore.Y).ShouldNotBe(0);

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

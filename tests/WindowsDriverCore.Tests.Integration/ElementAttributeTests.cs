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
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The <c>/attribute</c> surface against a real element.
/// </summary>
/// <remarks>
/// The expected strings come from a recorded WinAppDriver session against
/// <c>num5Button</c> in Calculator. Geometry is excluded on purpose: the
/// recorded rectangle depends on where the window happened to be, so its
/// <i>shape</i> is asserted rather than its numbers.
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class ElementAttributeTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;
    private string _five = null!;

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

        FindResult found = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");
        found.ElementIds.ShouldNotBeEmpty();
        _five = found.ElementIds[0];

        // A window that is still arriving reports bounds that change between two
        // reads, and several assertions below compare two readings. Wait for the
        // condition rather than assume it — see UiSettle for the flake this
        // fixes.
        UiSettle.UntilBoundsAreStable(_inspector, _window, _five);
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

    private string? Attribute(string name)
    {
        ElementRead<string?> read = _inspector.Attribute(_window, _five, name);
        read.Outcome.ShouldBe(ElementReadOutcome.Read);

        return read.Value;
    }

    [TestCase("Name", "Five")]
    [TestCase("AutomationId", "num5Button")]
    [TestCase("ClassName", "Button")]
    [TestCase("FrameworkId", "XAML")]
    [TestCase("LegacyName", "Five")]
    public void StringProperties_ComeBackVerbatim(string attribute, string expected) =>
        Attribute(attribute).ShouldBe(expected);

    [TestCase("IsEnabled", "True")]
    [TestCase("IsKeyboardFocusable", "True")]
    [TestCase("IsControlElement", "True")]
    [TestCase("IsContentElement", "True")]
    [TestCase("IsOffscreen", "False")]
    [TestCase("IsInvokePatternAvailable", "True")]
    [TestCase("IsSelectionItemPatternAvailable", "False")]
    [TestCase("IsValuePatternAvailable", "False")]
    [TestCase("SelectionItem.IsSelected", "False")]
    [TestCase("Value.IsReadOnly", "True")]
    public void BooleanProperties_ComeBackAsCapitalisedStrings(string attribute, string expected)
    {
        // Not JSON booleans. The same IsEnabled that /enabled reports as `true`
        // is "True" here — both polarities present, so an implementation that
        // hard-coded either string fails.
        Attribute(attribute).ShouldBe(expected);
    }

    [TestCase("ControlType", "ControlType.Button")]
    [TestCase("LocalizedControlType", "button")]
    [TestCase("Orientation", "None")]
    public void EnumeratedProperties_RenderAsNames_NotNumbers(string attribute, string expected)
    {
        // ControlType and LocalizedControlType are the pair this driver had
        // confused in its locator. Asserting both together keeps them from
        // sliding back into each other.
        Attribute(attribute).ShouldBe(expected);
    }

    [TestCase("HelpText")]
    [TestCase("AcceleratorKey")]
    [TestCase("AccessKey")]
    [TestCase("Value.Value")]
    public void UnsetStringProperties_AreNull_NotEmpty(string attribute)
    {
        // Measured: an empty string reads as absent through this route, which is
        // the opposite of /text, where an empty ValuePattern value answers "".
        Attribute(attribute).ShouldBeNull();
    }

    [TestCase("InvalidAttributeName")]
    [TestCase("name")]
    [TestCase("Value.Nonsense")]
    public void UnknownAttributeNames_AreNull_NotAnError(string attribute)
    {
        // Indistinguishable from an unset property, which is WinAppDriver's
        // behaviour rather than a shortcut. Note the lower-case "name": the
        // lookup is ordinal, like every other identifier comparison here.
        Attribute(attribute).ShouldBeNull();
    }

    [Test]
    public void RuntimeId_IsTheSameStringTheFinderIssued()
    {
        // The one attribute whose correct value this test already knows, because
        // the find produced it. A renderer that formatted integer arrays with
        // commas would fail here and nowhere else.
        Attribute("RuntimeId").ShouldBe(_five);
    }

    [Test]
    public void ProcessId_IsADecimalString()
    {
        string? processId = Attribute("ProcessId");

        processId.ShouldNotBeNull();
        int.TryParse(processId, out int parsed).ShouldBeTrue();
        parsed.ShouldBeGreaterThan(0);
    }

    [Test]
    public void BoundingRectangle_IsTheLabelledFormat_AndAgreesWithScreenBounds()
    {
        // The numbers depend on where the window is, so the check is against
        // this driver's own ScreenBounds rather than against a recorded literal.
        // That also makes it a cross-check: two paths to the same rectangle,
        // one through the attribute renderer and one through the bounds reader.
        ElementBounds bounds = _inspector.ScreenBounds(_window, _five).Value;

        Attribute("BoundingRectangle").ShouldBe(
            $"Left:{bounds.X} Top:{bounds.Y} Width:{bounds.Width} Height:{bounds.Height}");
    }

    [Test]
    public void ClickablePoint_IsCommaSeparated_AndSitsInsideTheElement()
    {
        // Comma, not the dots RuntimeId uses — two integer-ish properties with
        // two different separators, and getting them the same way round is the
        // kind of thing only an assertion catches.
        string? point = Attribute("ClickablePoint");

        point.ShouldNotBeNull();
        point.ShouldContain(",");
        point.ShouldNotContain(".");

        ElementBounds bounds = _inspector.ScreenBounds(_window, _five).Value;
        string[] parts = point.Split(',');

        int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(bounds.X, bounds.X + bounds.Width);
        int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(bounds.Y, bounds.Y + bounds.Height);
    }
}

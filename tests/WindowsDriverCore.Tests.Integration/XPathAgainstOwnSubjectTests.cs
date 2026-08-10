using System;
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
/// XPath, against a subject whose tree this repository controls.
/// </summary>
/// <remarks>
/// <para>
/// The engine is <c>System.Xml.XPath</c>, so these do not test XPath — they test
/// the <b>projection</b>: that the UIA tree becomes XML with the right element
/// names, the right attributes, and the right shape, and that matches come back
/// as element ids.
/// </para>
/// <para>
/// <b>The positional test is the one that matters.</b> Recorded from WinAppDriver
/// 1.2.1: <c>//Button[1]</c> matched eight elements and <c>(//Button)[1]</c>
/// matched one. That is correct XPath 1.0 — <c>//Button[1]</c> means "every
/// Button that is first among its siblings" — and it is the subtlety a
/// hand-written evaluator gets wrong. If the projection nests elements wrongly,
/// this is the test that notices.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class XPathAgainstOwnSubjectTests
{
    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchTestApp()
    {
        string? path = TestApp.Path;
        if (path is null)
        {
            Assert.Ignore("The WPF test subject has not been built.");
        }

        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(path, null, null));

        if (launched.Application is null)
        {
            Assert.Fail($"The test subject would not launch: {launched.FailureMessage}");
            return;
        }

        _window = launched.Application.WindowHandle;
        UiSettle.UntilBoundsAreStable(
            _inspector,
            _window,
            UiSettle.UntilSomethingMatches(
                _finder, _window, LocatorKind.AutomationId, "invokeOnly")[0]);
    }

    [OneTimeTearDown]
    public void CloseTestApp() => AppLifetime.KillAll(TestApp.ProcessName);

    private FindResult Select(string expression) =>
        _finder.FindAll(_window, LocatorKind.XPath, expression);

    private string NameOf(string elementId) =>
        _inspector.Attribute(_window, elementId, "Name").Value ?? string.Empty;

    [Test]
    public void AnExpressionMatchingByAutomationId_FindsTheElement()
    {
        FindResult found = Select("//Button[@AutomationId='invokeOnly']");

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.Count.ShouldBe(1);
        NameOf(found.ElementIds[0]).ShouldBe("Invoke only");
    }

    [Test]
    public void StartsWith_IsEvaluatedByTheEngine()
    {
        // The function the compatibility suite's alarm cleanup depends on. UIA
        // conditions cannot express it at all, so this only works because the
        // projection hands a real XPath engine a real document.
        FindResult found = Select("//Button[starts-with(@AutomationId, 'invoke')]");

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.Count.ShouldBe(1);
    }

    [Test]
    public void ABareIndexIsPerParent_AndAParenthesisedOneIsGlobal()
    {
        // Recorded from WinAppDriver: //Button[1] matched 8, (//Button)[1]
        // matched 1. The difference is the whole of XPath's positional rule, and
        // it only comes out right if the projection nests elements correctly.
        FindResult perParent = Select("//Button[1]");
        FindResult global = Select("(//Button)[1]");

        perParent.Failure.ShouldBe(FindFailure.None);
        global.Failure.ShouldBe(FindFailure.None);

        global.ElementIds.Count.ShouldBe(1);
        perParent.ElementIds.Count.ShouldBeGreaterThan(
            1,
            "//Button[1] selects the first Button under EACH parent, not the first in the document");
    }

    [Test]
    public void AnAbsolutePath_WalksTheProjectedShape()
    {
        // Fails if the projection is flat rather than nested — a flat document
        // would still answer // expressions correctly, so only a path with steps
        // can tell the two apart.
        FindResult found = Select("//*[@AutomationId='toggleHostingDisabled']/Button");

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.Count.ShouldBe(
            1, "the disabled Button is a CHILD of the CheckBox hosting it");
    }

    [Test]
    public void AnExpressionMatchingNothing_IsAnEmptyResultRatherThanAFailure()
    {
        FindResult found = Select("//Button[@AutomationId='NoSuchThing']");

        found.Failure.ShouldBe(FindFailure.None);
        found.ElementIds.ShouldBeEmpty();
    }

    [Test]
    public void AMalformedExpression_IsAnXPathLookupError()
    {
        // Recorded: WinAppDriver answers 500 for this, which the protocol layer
        // renders from this failure.
        Select("//Button[").Failure.ShouldBe(FindFailure.XPathLookupError);
    }

    [Test]
    public void AnUnknownFunction_IsAnXPathLookupError()
    {
        Select("//Button[bogus-function(@Name)]").Failure.ShouldBe(FindFailure.XPathLookupError);
    }
}

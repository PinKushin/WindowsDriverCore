using System;
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
/// Is a held <c>IUIAutomationElement</c> a live proxy or a snapshot?
/// </summary>
/// <remarks>
/// <para>
/// This settles whether the walk-per-command can be removed. Every element
/// command currently re-resolves its id by enumerating descendants, because UIA
/// rejects RuntimeId in a property condition. FlaUI never pays that, because it
/// hands the caller an element and reads properties straight off it.
/// </para>
/// <para>
/// The reason this driver does not do the same is a stated rule: no snapshot
/// held between calls, because a held view drifts from the live tree. <b>That
/// rule is about snapshots.</b> Whether an element reference obtained without a
/// cache request is a snapshot is a question about UIA, not about this driver,
/// and it has never been measured here — the rule was inherited alongside two
/// bug reports that <c>docs/FOUNDING-PREMISE.md</c> later retracted.
/// </para>
/// <para>
/// If the reference is live, holding one is not the defect the rule forbids, and
/// the tree walk is optional. If it is a snapshot, the walk is the price of
/// correctness and that is the end of it.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class HeldElementLivenessTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const int ElementNotAvailable = unchecked((int)0x80040201);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private CUIAutomationClass _automation = null!;
    private UiaElementFinder _finder = null!;
    private UiaElementResolver _resolver = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        _automation = new CUIAutomationClass();
        _finder = new UiaElementFinder(_automation, new UiaElementResolver(_automation));
        _resolver = new UiaElementResolver(_automation);
        _inspector = new UiaElementInspector(_automation, _resolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(CalculatorAumid, null, null));

        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;
        UiSettle.UntilBoundsAreStable(_inspector, _window, Find("num5Button"));
    }

    [OneTimeTearDown]
    public void CloseCalculator() => AppLifetime.KillAll("CalculatorApp");

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

    [Test]
    public void AHeldElement_ReportsPropertiesAsTheyAreNow_NotAsTheyWereWhenItWasFound()
    {
        // Manipulation: move the window, which changes the element's bounding
        // rectangle without touching the element itself.
        //
        // Prediction if the reference is LIVE: the held reference reports the new
        // rectangle, matching a freshly resolved one.
        // Prediction if it is a SNAPSHOT: the held reference reports the old
        // rectangle while the fresh one reports the new.
        //
        // The two predictions differ on the same observation, which is what makes
        // this an experiment rather than a demonstration.
        string elementId = Find("num5Button");

        using ElementLookupResult held = _resolver.Resolve(_window, elementId);
        held.Outcome.ShouldBe(ElementLookupOutcome.Resolved);
        held.Element.ShouldNotBeNull();

        tagRECT before = held.Element.CurrentBoundingRectangle;

        SetWindowPos(_window, 0, before.left + 90, before.top + 60, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate)
            .ShouldBeTrue("the window must move or nothing is being manipulated");

        // The control: a reference obtained after the move, through the path the
        // driver uses today.
        ElementBounds fresh = _inspector.ScreenBounds(_window, elementId).Value;
        fresh.X.ShouldNotBe(before.left, "the manipulation must have had an effect");

        tagRECT after = held.Element.CurrentBoundingRectangle;

        after.left.ShouldBe(
            fresh.X,
            "a held element that reports the pre-move rectangle is a snapshot, " +
            "and the walk-per-command is the price of correctness");
        after.top.ShouldBe(fresh.Y);
    }

    [Test]
    public void AHeldElement_KeepsItsIdentity_SoItCanBeValidatedCheaply()
    {
        // If handles are to be kept, every use must confirm the handle still
        // names the element the client asked for. GetRuntimeId on the held
        // reference is the check, and it has to survive an unrelated change to
        // the tree.
        string elementId = Find("num5Button");

        using ElementLookupResult held = _resolver.Resolve(_window, elementId);
        held.Element.ShouldNotBeNull();

        SetWindowPos(_window, 0, 200, 150, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate)
            .ShouldBeTrue();

        // Formatted here rather than through the driver's own helper: this test
        // is about what UIA reports, so it should not depend on the code under
        // discussion to say what the id is.
        int[] runtimeId = held.Element.GetRuntimeId();
        string.Join('.', runtimeId).ShouldBe(elementId);
    }

    [Test]
    public void AHeldElement_WhoseApplicationIsGone_Throws_RatherThanAnsweringStaleData()
    {
        // The failure mode that matters. A held reference must not keep answering
        // with the last value it saw once the element is gone — that is precisely
        // the "cached view drifting from the live tree" defect, and if it happens
        // here then handles cannot be kept whatever the first test says.
        //
        // Its own Calculator instance, because the manipulation destroys it.
        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(CalculatorAumid, null, null));

        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        nint doomed = launched.Application.WindowHandle;

        FindResult found = _finder.FindAll(doomed, LocatorKind.AutomationId, "num5Button");
        found.ElementIds.ShouldNotBeEmpty();

        using ElementLookupResult held = _resolver.Resolve(doomed, found.ElementIds[0]);
        held.Element.ShouldNotBeNull();

        // It answers now.
        held.Element.CurrentName.ShouldBe("Five");

        // Only this instance. Killing by name would destroy the fixture's shared
        // Calculator and couple this test to the order it runs in.
        AppLifetime.KillProcess(launched.Application.ProcessId);

        COMException? failure = Should.Throw<COMException>(
            () => _ = held.Element.CurrentName,
            "a held reference to a destroyed element must fail, not answer");

        TestContext.Out.WriteLine($"HRESULT 0x{failure.HResult:X8}");
        failure.HResult.ShouldBe(
            ElementNotAvailable,
            "UIA_E_ELEMENTNOTAVAILABLE is the signal a handle cache would map to " +
            "a stale element reference");
    }
}

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
/// The handle cache: it must be invisible in every answer and visible only in
/// the clock.
/// </summary>
/// <remarks>
/// It is an optimisation over <see cref="UiaElementResolver"/>, so the standard
/// it has to meet is that no observable answer changes. The tests below are
/// mostly about the ways a cache can be wrong rather than about it being fast.
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class CachingElementResolverTests : IDisposable
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private CUIAutomationClass _automation = null!;
    private UiaElementFinder _finder = null!;
    private UiaElementResolver _walking = null!;
    private CachingElementResolver _caching = null!;
    private nint _window;

    /// <summary>This fixture's own Calculator, killed by id rather than by name.</summary>
    private int _processId;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        _automation = new CUIAutomationClass();
        _finder = new UiaElementFinder(_automation, new UiaElementResolver(_automation));
        _walking = new UiaElementResolver(_automation);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(CalculatorAumid, null, null));

        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;
        _processId = launched.Application.ProcessId;

        UiSettle.UntilBoundsAreStable(
            new UiaElementInspector(_automation, _walking), _window, Find("num5Button"));
    }

    [SetUp]
    public void FreshCache() => _caching = new CachingElementResolver(_walking);

    [TearDown]
    public void DisposeCache() => Dispose();

    /// <summary>Releases the cache under test, and every handle it holds.</summary>
    public void Dispose() => _caching?.Dispose();

    [OneTimeTearDown]
    public void CloseCalculator()
    {
        // BY ID, not by name. This fixture launches its own Calculator because
        // it destroys windows deliberately; KillAll would also close the shared
        // instance other fixtures are using, and a developer's own Calculator.
        if (_processId != 0)
        {
            AppLifetime.KillProcess(_processId);
        }
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

    [Test]
    public void ASecondResolveOfTheSameElement_AnswersTheSameElement()
    {
        string elementId = Find("num5Button");

        using (ElementLookupResult first = _caching.Resolve(_window, elementId))
        {
            first.Outcome.ShouldBe(ElementLookupOutcome.Resolved);
            first.Element!.CurrentName.ShouldBe("Five");
        }

        // The first result has been disposed. A cache that let its user's Dispose
        // release the shared handle would fail here with a dead wrapper, which is
        // the exact mirror of the leak the previous implementation had.
        using ElementLookupResult second = _caching.Resolve(_window, elementId);

        second.Outcome.ShouldBe(ElementLookupOutcome.Resolved);
        second.Element!.CurrentName.ShouldBe("Five");
    }

    [Test]
    public void CachedAndUncachedResolvers_AgreeOnEveryElementInTheWindow()
    {
        // The cache is an optimisation, so the standard is that no answer
        // changes. Every button, both resolvers, same identity — a cache that
        // returned a neighbouring element for any of them fails here.
        FindResult buttons = _finder.FindAll(_window, LocatorKind.ControlType, "Button");
        buttons.ElementIds.Count.ShouldBeGreaterThan(5);

        foreach (string elementId in buttons.ElementIds)
        {
            using ElementLookupResult walked = _walking.Resolve(_window, elementId);
            using ElementLookupResult cached = _caching.Resolve(_window, elementId);

            cached.Outcome.ShouldBe(walked.Outcome, elementId);
            cached.Element!.CurrentName.ShouldBe(walked.Element!.CurrentName, elementId);
        }
    }

    [Test]
    public void AResolveThatFinds_Nothing_IsNotCachedAsAFailure()
    {
        // A miss must not be remembered, or an element that appears later would
        // stay invisible for the rest of the session. Resolve a missing id,
        // then a real one, then the missing one again.
        _caching.Resolve(_window, "99999.99999.99999").Outcome
            .ShouldBe(ElementLookupOutcome.NotFound);

        using ElementLookupResult real = _caching.Resolve(_window, Find("num5Button"));
        real.Outcome.ShouldBe(ElementLookupOutcome.Resolved);

        _caching.Resolve(_window, "99999.99999.99999").Outcome
            .ShouldBe(ElementLookupOutcome.NotFound);
    }

    [Test]
    public void Forget_ReleasesTheHandlesForAWindow()
    {
        _caching.Resolve(_window, Find("num5Button")).Dispose();
        _caching.Resolve(_window, Find("num7Button")).Dispose();
        _caching.Count.ShouldBe(2);

        _caching.Forget(_window);

        _caching.Count.ShouldBe(0);

        // And the resolver still works afterwards, by walking again.
        using ElementLookupResult again = _caching.Resolve(_window, Find("num5Button"));
        again.Outcome.ShouldBe(ElementLookupOutcome.Resolved);
        again.Element!.CurrentName.ShouldBe("Five");
    }

    [Test]
    public void TheCacheIsBounded()
    {
        // Each entry keeps a provider object alive inside Calculator, so an
        // unbounded table is a leak with a justification. Resolve more distinct
        // elements than the cap and check it holds.
        FindResult everything = _finder.FindAll(_window, LocatorKind.ControlType, "Button");

        foreach (string elementId in everything.ElementIds)
        {
            _caching.Resolve(_window, elementId).Dispose();
        }

        _caching.Count.ShouldBeLessThanOrEqualTo(CachingElementResolver.Capacity);
        _caching.Count.ShouldBeGreaterThan(0, "something must actually be cached");
    }

    [Test]
    public void AHandleWhoseElementIsGone_FallsBackToTheWalk_AndReportsNotFound()
    {
        // The failure the identity check exists for. Its own application,
        // because the manipulation destroys it.
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
        string elementId = found.ElementIds[0];

        _caching.Resolve(doomed, elementId).Dispose();
        _caching.Count.ShouldBeGreaterThan(0, "the handle must be cached for this to test anything");

        // Only this instance. Killing by process name would take the fixture's
        // shared Calculator with it and fail every test that ran afterwards.
        AppLifetime.KillProcess(launched.Application.ProcessId);

        // Wait for the window to actually go, not just the process. The two are
        // separate events and the gap widens with load — under an Appium run this
        // assertion saw the element still resolving. Waiting here completes the
        // manipulation; the measurement below still happens exactly once.
        AppLifetime.WaitUntilWindowIsGone(doomed);

        AppLifetime.WindowExists(doomed).ShouldBeFalse("the manipulation must have landed");

        // **The cached path and a fresh walk must agree.** They did not: the walk
        // reported NoSuchWindow while the cache handed back a resolved element
        // from the closed window, because the handle was validated with
        // GetRuntimeId — which the proxy answers without contacting the
        // application. A cache is only allowed to be faster, never to give a
        // different answer.
        ElementLookupOutcome viaWalk = _walking.Resolve(doomed, elementId).Outcome;
        ElementLookupOutcome viaCache = _caching.Resolve(doomed, elementId).Outcome;

        viaCache.ShouldBe(
            viaWalk,
            "the cache must answer exactly what a fresh walk answers");
        viaCache.ShouldBe(ElementLookupOutcome.NoSuchWindow);
    }
}

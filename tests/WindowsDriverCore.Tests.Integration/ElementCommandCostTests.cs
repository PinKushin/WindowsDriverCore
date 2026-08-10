using System;
using System.Diagnostics;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// What a property read costs, with and without the handle cache.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes is the one FlaUI does not have: it holds the element, so
/// a property read is one cross-process call, while resolving an id means
/// walking the tree because UIA rejects RuntimeId in a property condition.
/// </para>
/// <para>
/// Reported rather than asserted against a threshold. A wall-clock number
/// baked into an assertion fails on a slower machine and teaches nobody
/// anything; the ratio is what the change is about, and it is printed.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("Performance")]
[NonParallelizable]
public sealed class ElementCommandCostTests : IDisposable
{
    private const int Samples = 20;

    private CUIAutomationClass _automation = null!;
    private UiaElementFinder _finder = null!;
    private UiaElementResolver _walking = null!;
    private CachingElementResolver _caching = null!;
    private nint _window;
    private string _element = null!;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        _automation = new CUIAutomationClass();
        _finder = new UiaElementFinder(_automation, new UiaElementResolver(_automation));
        _walking = new UiaElementResolver(_automation);
        _caching = new CachingElementResolver(_walking);

        // One Calculator for the whole run. See SharedCalculator.
        _window = SharedCalculator.Window();
        if (_window == 0)
        {
            Assert.Ignore("Calculator is not available.");
        }

        FindResult found = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");
        found.ElementIds.ShouldNotBeEmpty();
        _element = found.ElementIds[0];

        UiSettle.UntilBoundsAreStable(
            new UiaElementInspector(_automation, _walking), _window, _element);
    }

    [OneTimeTearDown]
    public void ReleaseCache()
    {
        // The Calculator is shared and outlives this fixture, so only the cache
        // is released here. Killing it by name would close the instance other
        // fixtures are still using.
        Dispose();
    }

    /// <summary>Releases the cache and its handles.</summary>
    public void Dispose() => _caching?.Dispose();

    [Test]
    public void APropertyRead_CostsFarLessWhenTheHandleIsHeld()
    {
        UiaElementInspector walking = new(_automation, _walking);
        UiaElementInspector caching = new(_automation, _caching);

        // Warm both: first call pays JIT and first-call COM setup, which would
        // otherwise land entirely on whichever ran first.
        walking.Text(_window, _element).Outcome.ShouldBe(ElementReadOutcome.Read);
        caching.Text(_window, _element).Outcome.ShouldBe(ElementReadOutcome.Read);

        double walkingMs = Measure(walking);
        double cachingMs = Measure(caching);

        TestContext.Out.WriteLine(
            $"property read: walking {walkingMs:F2} ms, cached handle {cachingMs:F2} ms, " +
            $"{walkingMs / cachingMs:F1}x");

        // Not a threshold on either number — only that the cached path is the
        // faster one, which is the entire claim being made. If it is not, the
        // cache is costing more than the walk it replaces and should go.
        cachingMs.ShouldBeLessThan(
            walkingMs,
            "holding the element must beat re-walking the tree, or the cache is not worth its risk");

        Assert.Pass(
            $"Walking {walkingMs:F2} ms, cached {cachingMs:F2} ms, {walkingMs / cachingMs:F1}x faster.");
    }

    private double Measure(UiaElementInspector inspector)
    {
        Stopwatch clock = Stopwatch.StartNew();

        for (int sample = 0; sample < Samples; sample++)
        {
            inspector.Text(_window, _element).Outcome.ShouldBe(ElementReadOutcome.Read);
        }

        clock.Stop();

        return clock.Elapsed.TotalMilliseconds / Samples;
    }
}

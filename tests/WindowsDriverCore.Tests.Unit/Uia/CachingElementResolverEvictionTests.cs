using System.Collections.Generic;
using System.Globalization;
using Interop.UIAutomationClient;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Uia;

namespace WindowsDriverCore.Tests.Unit.Uia;

/// <summary>
/// The handle cache's eviction path, which nothing had ever reached.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CachingElementResolver"/> holds at most
/// <see cref="CachingElementResolver.Capacity"/> handles and evicts the least
/// recently used. Every integration fixture builds a fresh resolver and touches
/// a handful of elements, so the eviction branch was live code with no coverage
/// at all.
/// </para>
/// <para>
/// It matters because the arrangement the Appium documentation recommends — one
/// session for a whole suite — is exactly the one that reaches it. A suite that
/// works with more than a few hundred distinct elements evicts constantly, and
/// until now nothing said what happened when it did.
/// </para>
/// <para>
/// Headless: the inner resolver is substituted, so this measures the cache's
/// bookkeeping rather than UI Automation.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CachingElementResolverEvictionTests
{
    private const nint Window = 0x1234;

    private static string ElementId(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"42.100.4.{index}");

    /// <summary>
    /// An element that answers <c>GetRuntimeId</c>, which is what the cache
    /// checks on every hit before trusting a handle.
    /// </summary>
    private static IUIAutomationElement ElementWithId(int index)
    {
        IUIAutomationElement element = Substitute.For<IUIAutomationElement>();
        element.GetRuntimeId().Returns([42, 100, 4, index]);

        return element;
    }

    private static IElementResolver InnerServing(Dictionary<string, IUIAutomationElement> elements)
    {
        IElementResolver inner = Substitute.For<IElementResolver>();
        inner.Resolve(Arg.Any<nint>(), Arg.Any<string>()).Returns(call =>
            elements.TryGetValue(call.ArgAt<string>(1), out IUIAutomationElement? element)
                ? ElementLookupResult.Resolved(element)
                : ElementLookupResult.Failed(ElementLookupOutcome.NotFound));

        return inner;
    }

    [Test]
    public void TheCacheNeverExceedsItsCapacity()
    {
        Dictionary<string, IUIAutomationElement> elements = [];
        for (int index = 0; index < CachingElementResolver.Capacity * 2; index++)
        {
            elements[ElementId(index)] = ElementWithId(index);
        }

        using CachingElementResolver cache = new(InnerServing(elements));

        foreach (string id in elements.Keys)
        {
            cache.Resolve(Window, id).Dispose();
        }

        cache.Count.ShouldBe(
            CachingElementResolver.Capacity,
            "a session working with more elements than the cap must not grow past it");
    }

    [Test]
    public void EvictionTakesTheLeastRecentlyUsed_NotTheOldest()
    {
        // The property that makes least-recently-used worth having over a plain
        // clear-when-full. A control that stays in use must survive a flood of
        // one-off lookups. A first-in-first-out cache would drop it and re-walk
        // the tree every time, which is the cost this class exists to avoid.
        Dictionary<string, IUIAutomationElement> elements = [];
        for (int index = 0; index < CachingElementResolver.Capacity + 50; index++)
        {
            elements[ElementId(index)] = ElementWithId(index);
        }

        IElementResolver inner = InnerServing(elements);
        using CachingElementResolver cache = new(inner);

        string kept = ElementId(0);
        cache.Resolve(Window, kept).Dispose();

        // Fill past the cap while touching the kept element throughout, so it
        // stays the most recently used.
        for (int index = 1; index < CachingElementResolver.Capacity + 50; index++)
        {
            cache.Resolve(Window, ElementId(index)).Dispose();
            cache.Resolve(Window, kept).Dispose();
        }

        // **How many times the tree was walked for it**, across the whole run —
        // not whether it happens to be cached at the end.
        //
        // An earlier version asked the latter and survived a mutation that
        // turned this cache into first-in-first-out. Under that mutation the
        // kept element is evicted, immediately re-resolved on the next touch,
        // and re-added, so it is usually present when the run finishes. The
        // observation did not distinguish the two policies at all. This one
        // does: least-recently-used walks once, first-in-first-out walks every
        // time it is evicted.
        inner.Received(1).Resolve(Window, kept);
    }

    [Test]
    public void AnEvictedElementStillResolves_ByWalkingAgain()
    {
        // Eviction is a performance event, not a correctness one. The answer
        // must be identical either way — which is the whole justification for
        // the cache being allowed to exist.
        Dictionary<string, IUIAutomationElement> elements = [];
        for (int index = 0; index < CachingElementResolver.Capacity + 10; index++)
        {
            elements[ElementId(index)] = ElementWithId(index);
        }

        IElementResolver inner = InnerServing(elements);
        using CachingElementResolver cache = new(inner);

        foreach (string id in elements.Keys)
        {
            cache.Resolve(Window, id).Dispose();
        }

        string evicted = ElementId(0);
        inner.ClearReceivedCalls();

        using ElementLookupResult again = cache.Resolve(Window, evicted);

        again.Outcome.ShouldBe(ElementLookupOutcome.Resolved);
        again.Element.ShouldBeSameAs(elements[evicted]);
        inner.Received(1).Resolve(Window, evicted);
    }

    [Test]
    public void AHandleWhoseIdentityChanged_IsEvictedAndResolvedAgain()
    {
        // The check that makes holding a handle safe. If the element behind a
        // cached handle stops being the element the caller asked for, the entry
        // must be dropped rather than served.
        IUIAutomationElement drifted = Substitute.For<IUIAutomationElement>();
        drifted.GetRuntimeId().Returns([42, 100, 4, 0]);

        Dictionary<string, IUIAutomationElement> elements = new()
        {
            [ElementId(0)] = drifted,
        };

        IElementResolver inner = InnerServing(elements);
        using CachingElementResolver cache = new(inner);

        cache.Resolve(Window, ElementId(0)).Dispose();
        cache.Count.ShouldBe(1);

        // The handle now names a different element than the id it is filed under.
        drifted.GetRuntimeId().Returns([42, 100, 4, 999]);

        inner.ClearReceivedCalls();
        cache.Resolve(Window, ElementId(0)).Dispose();

        inner.Received(1).Resolve(Window, ElementId(0));
    }

    [Test]
    public void ForgettingOneWindow_LeavesAnotherWindowsHandlesAlone()
    {
        // Sessions are deleted independently, and two can drive different
        // windows at once. The bystander is the point: a Forget that cleared
        // everything would pass a test that only checked the target.
        Dictionary<string, IUIAutomationElement> elements = new()
        {
            [ElementId(1)] = ElementWithId(1),
            [ElementId(2)] = ElementWithId(2),
        };

        IElementResolver inner = InnerServing(elements);
        using CachingElementResolver cache = new(inner);

        cache.Resolve(Window, ElementId(1)).Dispose();
        cache.Resolve(0x5678, ElementId(2)).Dispose();
        cache.Count.ShouldBe(2);

        cache.Forget(Window);

        cache.Count.ShouldBe(1, "only the forgotten window's handles go");

        inner.ClearReceivedCalls();
        cache.Resolve(0x5678, ElementId(2)).Dispose();
        inner.DidNotReceive().Resolve(0x5678, ElementId(2));
    }

    [Test]
    public void DisposingReleasesEverything()
    {
        Dictionary<string, IUIAutomationElement> elements = [];
        for (int index = 0; index < 10; index++)
        {
            elements[ElementId(index)] = ElementWithId(index);
        }

        CachingElementResolver cache = new(InnerServing(elements));

        foreach (string id in elements.Keys)
        {
            cache.Resolve(Window, id).Dispose();
        }

        cache.Count.ShouldBe(10);

        cache.Dispose();

        cache.Count.ShouldBe(0);
    }

    [Test]
    public void AResultTheCacheOwns_SurvivesItsCallerDisposing()
    {
        // Borrowed lifetime, which is what stops the first caller's `using` from
        // releasing a handle the cache is still holding. Without it the second
        // use would be a call on a dead wrapper.
        Dictionary<string, IUIAutomationElement> elements = new()
        {
            [ElementId(7)] = ElementWithId(7),
        };

        IElementResolver inner = InnerServing(elements);
        using CachingElementResolver cache = new(inner);

        cache.Resolve(Window, ElementId(7)).Dispose();

        inner.ClearReceivedCalls();
        using ElementLookupResult second = cache.Resolve(Window, ElementId(7));

        second.Element.ShouldBeSameAs(elements[ElementId(7)]);
        inner.DidNotReceive().Resolve(Window, ElementId(7));
    }
}

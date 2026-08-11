using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Which view of the UI Automation tree a <c>FindAll</c> walks — MEASURED, and
/// measured against the reference driver rather than against an expectation.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED 2026-08-11, Windows 11 host, Calculator:</b>
/// <c>IUIAutomationElement::FindAll</c> with a <i>true</i> condition over
/// <c>TreeScope_Descendants</c> reaches <b>73</b> elements where a raw
/// <c>TreeWalker</c> descent from the same root reaches <b>125</b>. The 73 are a
/// strict subset — nothing is reachable by find and not by the walk — and every
/// one of the 52 it misses has <c>IsControlElement=False</c>. So <c>FindAll</c>
/// filters to the <b>control view</b>, and that includes elements carrying real
/// automation ids: <c>AppIcon</c>, <c>TextContainer</c>, <c>NormalOutput</c>,
/// <c>ParenthesisCount</c>.
/// </para>
/// <para>
/// <b>That mechanism is real and it is NOT a defect, because WinAppDriver has
/// it too.</b> Measured the same day on the same host through WinAppDriver
/// 1.2.1: <c>num5Button</c> found, and <c>NormalOutput</c>,
/// <c>TextContainer</c>, <c>ParenthesisCount</c> and <c>AppIcon</c> all
/// <i>not found</i> — its <c>GET /source</c> omits all four as well. Matching
/// the control view is therefore parity; widening to the raw view would be a
/// silent divergence that doubles what <c>//Text</c> matches and what
/// <c>/source</c> emits.
/// </para>
/// <para>
/// <b>This fixture exists because the mechanism was about to be credited with a
/// cause.</b> <c>docs/LIMITATIONS.md</c> proposed control-view filtering as the
/// reason eleven <c>*Error_StaleElement</c> tests cannot find
/// <c>AlarmSaveButton</c>. The first two tests below confirm the mechanism; the
/// WinAppDriver measurement above refutes it as the cause, since the reference
/// driver shares the limitation and still finds that button on the guest.
/// Confirming a mechanism is not confirming it did anything.
/// </para>
/// <para>
/// <b>The condition is chosen so correct and broken differ.</b> A property
/// condition would confound two questions — a missing match could be the view or
/// the property. A true condition matches everything the search can reach, so the
/// only thing left that can shrink the result is the view. The raw walker is the
/// control: same root, same subtree, no filtering by construction.
/// </para>
/// <para>
/// <b>The subject must be a packaged application.</b> A plain WPF window is
/// almost entirely control-view elements, so raw and control agree there and the
/// experiment would be insensitive to the manipulation — it would pass whichever
/// way UIA behaves. Calculator's XAML tree carries the non-control scaffolding
/// this is looking for.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class WhichViewAFindWalksTests
{
    /// <summary>UIA_IsControlElementPropertyId.</summary>
    private const int IsControlElementProperty = 30016;

    /// <summary>UIA_IsContentElementPropertyId.</summary>
    private const int IsContentElementProperty = 30017;

    private const int NameProperty = 30005;
    private const int AutomationIdProperty = 30011;
    private const int ClassNameProperty = 30012;
    private const int ControlTypeProperty = 30003;

    /// <summary>
    /// A raw walk can descend forever into a pathological tree. This bounds the
    /// instrument, not the subject, and the test says so when it is hit rather
    /// than quietly reporting a short walk as a complete one.
    /// </summary>
    private const int MaximumElements = 20000;

    private static readonly CUIAutomationClass Automation = new();

    [Test]
    public void AFindAll_WalksTheControlView_AndTheRawTreeIsStrictlyLarger()
    {
        nint window = SharedDriverSession.Window();
        if (window == 0)
        {
            Assert.Ignore("Calculator is not available.");
            return;
        }

        IUIAutomationElement root = Automation.ElementFromHandle(window);
        root.ShouldNotBeNull("the session's window must resolve to an element");

        Dictionary<string, string> byFind = [];
        Dictionary<string, string> byRawWalk = [];

        try
        {
            IUIAutomationCondition anything = Automation.CreateTrueCondition();
            try
            {
                CollectFromFindAll(root, anything, byFind);
            }
            finally
            {
                Marshal.ReleaseComObject(anything);
            }

            CollectFromRawWalk(root, byRawWalk);
        }
        finally
        {
            Marshal.ReleaseComObject(root);
        }

        string[] rawOnly = [.. byRawWalk.Keys.Except(byFind.Keys).OrderBy(id => id, StringComparer.Ordinal)];
        string[] findOnly = [.. byFind.Keys.Except(byRawWalk.Keys).OrderBy(id => id, StringComparer.Ordinal)];

        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"FindAll(TreeScope_Descendants, true) = {byFind.Count} elements"));
        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"raw TreeWalker descent            = {byRawWalk.Count} elements"));
        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"reachable ONLY by the raw walk    = {rawOnly.Length}"));
        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"reachable ONLY by FindAll         = {findOnly.Length}"));

        foreach (string id in rawOnly.Take(60))
        {
            TestContext.Out.WriteLine($"  raw-only  {byRawWalk[id]}");
        }

        foreach (string id in findOnly.Take(20))
        {
            TestContext.Out.WriteLine($"  find-only {byFind[id]}");
        }

        // Containment, in both directions. "The raw walk found more" alone would
        // also be satisfied by two overlapping sets, which would mean the two are
        // different views rather than one being a filter of the other.
        findOnly.ShouldBeEmpty(
            "the control view must be a SUBSET of the raw view; anything reachable by " +
            "find and not by a raw walk would mean these are not nested views at all " +
            "and the walker is the thing that is wrong");

        rawOnly.ShouldNotBeEmpty(
            "this subject must have non-control elements or the comparison is " +
            "insensitive to the manipulation and cannot distinguish a filtered find " +
            "from an unfiltered one");
    }

    /// <summary>
    /// Whether the difference — if there is one — is explained by the control view.
    /// </summary>
    /// <remarks>
    /// Separate from the test above on purpose. That one measures <i>whether</i>
    /// the two disagree; this one measures <i>why</i>, and conflating them would
    /// leave a failure that names a symptom with no cause attached. If the sets
    /// agree this is vacuous and says so rather than passing quietly.
    /// </remarks>
    [Test]
    public void WhateverAFindAllMisses_IsNotAControlElement()
    {
        nint window = SharedDriverSession.Window();
        if (window == 0)
        {
            Assert.Ignore("Calculator is not available.");
            return;
        }

        IUIAutomationElement root = Automation.ElementFromHandle(window);

        HashSet<string> byFind = [];
        List<(string Id, string Describe, bool IsControl, bool IsContent)> raw = [];

        try
        {
            IUIAutomationCondition anything = Automation.CreateTrueCondition();
            try
            {
                Dictionary<string, string> found = [];
                CollectFromFindAll(root, anything, found);
                byFind = [.. found.Keys];
            }
            finally
            {
                Marshal.ReleaseComObject(anything);
            }

            WalkRaw(root, element =>
            {
                string? id = RuntimeIdOf(element);
                if (id is null)
                {
                    return;
                }

                raw.Add((id, Describe(element), Flag(element, IsControlElementProperty),
                    Flag(element, IsContentElementProperty)));
            });
        }
        finally
        {
            Marshal.ReleaseComObject(root);
        }

        (string Id, string Describe, bool IsControl, bool IsContent)[] missed =
            [.. raw.Where(entry => !byFind.Contains(entry.Id))];

        if (missed.Length == 0)
        {
            Assert.Inconclusive(
                "FindAll reached every element the raw walk did, so there is nothing to " +
                "explain and this test measures nothing. That is itself the answer: the " +
                "control-view hypothesis is refuted and the stale-element cluster has " +
                "another cause.");
            return;
        }

        foreach ((string _, string describe, bool isControl, bool isContent) in missed.Take(60))
        {
            TestContext.Out.WriteLine(
                $"  missed  IsControlElement={isControl} IsContentElement={isContent}  {describe}");
        }

        missed.ShouldAllBe(
            entry => !entry.IsControl,
            "if FindAll drops elements that ARE control elements then the view is not the " +
            "explanation and something else is filtering the search");
    }

    /// <summary>
    /// Where the filtering comes from, and the lever that would remove it —
    /// deliberately not pulled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cache request carries a <c>TreeFilter</c> whose default is the control
    /// view, and that is the whole of the mechanism the tests above measure. The
    /// non-caching <c>FindAll</c> uses a default request, so there is no way to
    /// widen it; <c>FindAllBuildCache</c> takes one, so there is.
    /// </para>
    /// <para>
    /// <b>Kept as a test rather than taken as a fix.</b> WinAppDriver was measured
    /// to have the same limitation, so pulling this lever would make finds and
    /// <c>/source</c> disagree with the reference driver — 125 nodes where it
    /// emits 73. This documents that the choice is a choice, with the cost known,
    /// so a future session reaching for the raw view finds the measurement instead
    /// of rediscovering it.
    /// </para>
    /// <para>
    /// The prediction is exact rather than directional: a true tree filter must
    /// reach the SAME set as the raw walk, not merely more than the control view.
    /// "More" would also be satisfied by the content view, by a partial widening,
    /// or by a subtree that happened to grow between the two reads.
    /// </para>
    /// </remarks>
    [Test]
    public void ATrueTreeFilter_WouldReachTheWholeRawTree()
    {
        nint window = SharedDriverSession.Window();
        if (window == 0)
        {
            Assert.Ignore("Calculator is not available.");
            return;
        }

        IUIAutomationElement root = Automation.ElementFromHandle(window);

        Dictionary<string, string> byRawWalk = [];
        Dictionary<string, string> byFilteredFind = [];
        int controlViewCount;

        try
        {
            CollectFromRawWalk(root, byRawWalk);

            IUIAutomationCondition anything = Automation.CreateTrueCondition();
            try
            {
                Dictionary<string, string> control = [];
                CollectFromFindAll(root, anything, control);
                controlViewCount = control.Count;

                IUIAutomationCacheRequest request = Automation.CreateCacheRequest();

                // The manipulation, and the only line that differs from the
                // failing test above.
                request.TreeFilter = anything;

                // Full, or the matched elements cannot answer GetRuntimeId —
                // RuntimeId is not cacheable, so a mode of None would leave every
                // match unidentifiable and the comparison would measure nothing.
                request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;

                IUIAutomationElementArray? matches = root.FindAllBuildCache(
                    TreeScope.TreeScope_Descendants, anything, request);

                if (matches is not null)
                {
                    try
                    {
                        for (int index = 0; index < matches.Length; index++)
                        {
                            IUIAutomationElement element = matches.GetElement(index);
                            try
                            {
                                string? id = RuntimeIdOf(element);
                                if (id is not null)
                                {
                                    byFilteredFind[id] = Describe(element);
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(element);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(matches);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(anything);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(root);
        }

        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"control view (default TreeFilter) = {controlViewCount}"));
        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"true TreeFilter                   = {byFilteredFind.Count}"));
        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"raw TreeWalker descent            = {byRawWalk.Count}"));

        // The control. Without it "the true filter reached 125" is satisfied by a
        // build in which the control view also reached 125 — that is, by the
        // manipulation having no effect on a subject that could not show one.
        controlViewCount.ShouldBeLessThan(
            byRawWalk.Count,
            "the default filter must be the narrower one, or this subject cannot " +
            "distinguish the two and the test proves nothing");

        byFilteredFind.Keys.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(
            byRawWalk.Keys.OrderBy(id => id, StringComparer.Ordinal),
            "a true tree filter must reach exactly the raw tree");
    }

    private static void CollectFromFindAll(
        IUIAutomationElement root,
        IUIAutomationCondition condition,
        Dictionary<string, string> into)
    {
        IUIAutomationElementArray? matches =
            root.FindAll(TreeScope.TreeScope_Descendants, condition);

        if (matches is null)
        {
            return;
        }

        try
        {
            for (int index = 0; index < matches.Length; index++)
            {
                IUIAutomationElement element = matches.GetElement(index);
                try
                {
                    string? id = RuntimeIdOf(element);
                    if (id is not null)
                    {
                        into[id] = Describe(element);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(element);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(matches);
        }
    }

    private static void CollectFromRawWalk(
        IUIAutomationElement root, Dictionary<string, string> into) =>
        WalkRaw(root, element =>
        {
            string? id = RuntimeIdOf(element);
            if (id is not null)
            {
                into[id] = Describe(element);
            }
        });

    /// <summary>
    /// Descends the raw view from <paramref name="root"/>, excluding the root, and
    /// hands every descendant to <paramref name="visit"/>.
    /// </summary>
    /// <remarks>
    /// The root is excluded because <c>TreeScope_Descendants</c> excludes it, and
    /// comparing a set that contains it against one that does not would report a
    /// difference of exactly one element that means nothing.
    /// </remarks>
    private static void WalkRaw(IUIAutomationElement root, Action<IUIAutomationElement> visit)
    {
        IUIAutomationTreeWalker walker = Automation.RawViewWalker;
        int seen = 0;

        Descend(root);

        void Descend(IUIAutomationElement parent)
        {
            IUIAutomationElement? child = walker.GetFirstChildElement(parent);

            while (child is not null)
            {
                if (++seen > MaximumElements)
                {
                    Marshal.ReleaseComObject(child);
                    Assert.Fail(
                        $"The raw walk passed {MaximumElements} elements. The bound is on " +
                        "the instrument, so this result is a truncated walk rather than a " +
                        "complete one and must not be compared against anything.");
                    return;
                }

                visit(child);
                Descend(child);

                IUIAutomationElement? next = walker.GetNextSiblingElement(child);
                Marshal.ReleaseComObject(child);
                child = next;
            }
        }
    }

    private static string? RuntimeIdOf(IUIAutomationElement element)
    {
        try
        {
            int[]? id = element.GetRuntimeId();
            return id is null
                ? null
                : string.Join(".", id.Select(part => part.ToString(CultureInfo.InvariantCulture)));
        }
        catch (COMException)
        {
            // The element went away mid-walk. Not a defect and not a result:
            // it simply cannot be compared, so it is left out of both sets.
            return null;
        }
    }

    private static string Describe(IUIAutomationElement element) => string.Create(
        CultureInfo.InvariantCulture,
        $"<{Text(element, ControlTypeProperty)}> " +
        $"AutomationId='{Text(element, AutomationIdProperty)}' " +
        $"Name='{Text(element, NameProperty)}' " +
        $"ClassName='{Text(element, ClassNameProperty)}'");

    private static string Text(IUIAutomationElement element, int property)
    {
        try
        {
            object? value = element.GetCurrentPropertyValue(property);
            return value?.ToString() ?? string.Empty;
        }
        catch (COMException)
        {
            return "<unavailable>";
        }
    }

    private static bool Flag(IUIAutomationElement element, int property)
    {
        try
        {
            return element.GetCurrentPropertyValue(property) is bool value && value;
        }
        catch (COMException)
        {
            return false;
        }
    }
}

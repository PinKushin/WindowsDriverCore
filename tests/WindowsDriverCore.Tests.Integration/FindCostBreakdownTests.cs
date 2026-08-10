using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;

using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Where the time in a find actually goes.
/// </summary>
/// <remarks>
/// <para>
/// This exists to settle an argument with a number rather than an opinion: does
/// a native shim, or a different language, have anything to offer here? If the
/// UI Automation calls dominate, then the answer is "do fewer UIA calls", and
/// marshalling — which is most of what C would buy back — is noise.
/// </para>
/// <para>
/// It measures rather than asserts a threshold. A performance assertion tied to
/// a wall-clock number would fail on a slower machine and tell nobody anything.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("Performance")]
[NonParallelizable]
public sealed class FindCostBreakdownTests
{
    private const int Samples = 10;

    private nint _window;
    private CUIAutomationClass _automation = null!;

    [OneTimeSetUp]
    public void LaunchCalculator()
    {
        _automation = new CUIAutomationClass();

        // Shared, and opened through the driver: the session owns the
        // application's lifetime. See SharedDriverSession.
        _window = SharedDriverSession.Window();
        if (_window == 0)
        {
            Assert.Ignore("Calculator is not available.");
        }
    }
    [Test]
    public void FindCost_SplitsBetweenTheSearchCallAndReadingEachMatch()
    {
        // A locator that matches many elements, because the N+1 only shows up
        // when N is large. Matching one element would make the per-match cost
        // invisible — the same "effect size below resolution" trap as before.
        IUIAutomation automation = _automation;

        double searchMs = 0;
        double readIdsMs = 0;
        int matchCount = 0;

        for (int sample = 0; sample < Samples; sample++)
        {
            IUIAutomationElement root = automation.ElementFromHandle(_window);
            IUIAutomationCondition condition = automation.CreatePropertyCondition(
                30003 /* ControlType */, 50000 /* Button */);

            Stopwatch search = Stopwatch.StartNew();
            IUIAutomationElementArray matches = root.FindAll(
                TreeScope.TreeScope_Descendants, condition);
            int length = matches.Length;
            search.Stop();

            Stopwatch readIds = Stopwatch.StartNew();
            for (int index = 0; index < length; index++)
            {
                IUIAutomationElement element = matches.GetElement(index);

                // The N+1: one cross-process call per match, on top of the search.
                int[] runtimeId = element.GetRuntimeId();
                _ = runtimeId.Length;

                Marshal.ReleaseComObject(element);
            }

            readIds.Stop();

            searchMs += search.Elapsed.TotalMilliseconds;
            readIdsMs += readIds.Elapsed.TotalMilliseconds;
            matchCount = length;

            Marshal.ReleaseComObject(condition);
            Marshal.ReleaseComObject(root);
        }

        double searchAvg = searchMs / Samples;
        double readAvg = readIdsMs / Samples;
        double total = searchAvg + readAvg;

        TestContext.Out.WriteLine(
            $"matches={matchCount}  search={searchAvg:F2}ms  readIds={readAvg:F2}ms  " +
            $"total={total:F2}ms  readIds share={readAvg / total:P0}");

        matchCount.ShouldBeGreaterThan(
            5, "the condition must match many elements or the per-match cost is invisible");

        Assert.Pass(
            $"{matchCount} matches: search {searchAvg:F2}ms, per-match id reads {readAvg:F2}ms " +
            $"({readAvg / total:P0} of total).");
    }

    [Test]
    public void FindCost_ManagedOverheadVersusTheUiaCallsThemselves()
    {
        // The language question, measured. Everything our code does around the
        // COM calls — allocating the result list, formatting ids, building the
        // condition — against the COM calls themselves.
        UiaElementFinder finder = new(_automation, new UiaElementResolver(_automation));

        // Warm up so JIT and first-call COM setup do not land in the sample.
        finder.FindAll(_window, LocatorKind.ControlType, "Button");

        Stopwatch whole = Stopwatch.StartNew();
        for (int sample = 0; sample < Samples; sample++)
        {
            FindResult result = finder.FindAll(_window, LocatorKind.ControlType, "Button");
            result.Failure.ShouldBe(FindFailure.None);
        }

        whole.Stop();

        // The same work with the COM calls only, no result building.
        IUIAutomation automation = _automation;
        Stopwatch comOnly = Stopwatch.StartNew();
        for (int sample = 0; sample < Samples; sample++)
        {
            IUIAutomationElement root = automation.ElementFromHandle(_window);
            IUIAutomationCondition condition = automation.CreatePropertyCondition(30003, 50000);
            IUIAutomationElementArray matches = root.FindAll(
                TreeScope.TreeScope_Descendants, condition);

            for (int index = 0; index < matches.Length; index++)
            {
                IUIAutomationElement element = matches.GetElement(index);
                _ = element.GetRuntimeId();
                Marshal.ReleaseComObject(element);
            }

            Marshal.ReleaseComObject(condition);
            Marshal.ReleaseComObject(root);
        }

        comOnly.Stop();

        double wholeAvg = whole.Elapsed.TotalMilliseconds / Samples;
        double comAvg = comOnly.Elapsed.TotalMilliseconds / Samples;
        double managedShare = Math.Max(0, wholeAvg - comAvg) / wholeAvg;

        TestContext.Out.WriteLine(
            $"whole find={wholeAvg:F2}ms  COM calls alone={comAvg:F2}ms  " +
            $"managed overhead={managedShare:P0}");

        Assert.Pass(
            $"Whole find {wholeAvg:F2}ms, COM alone {comAvg:F2}ms, " +
            $"managed overhead {managedShare:P0}. A shim can only address the managed share.");
    }
}

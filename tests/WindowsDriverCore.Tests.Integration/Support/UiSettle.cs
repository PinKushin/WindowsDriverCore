using System;
using System.Diagnostics;
using NUnit.Framework;
using WindowsDriverCore.Automation;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// Waits until an element's geometry has stopped changing.
/// </summary>
/// <remarks>
/// <para>
/// A freshly launched window is still arriving: it animates in, it may be
/// positioned after it is created, and UI Automation will happily report the
/// bounds it has at that instant. A test that reads a rectangle twice and
/// compares the two readings can therefore fail for a reason that has nothing to
/// do with the code under test.
/// </para>
/// <para>
/// That happened once here — one attribute test failed on the first run after a
/// launch and passed on three consecutive re-runs. <b>A test that passes most of
/// the time is a statement about the test, not about the code</b>, so the fix is
/// synchronisation rather than a retry: wait for the condition the measurement
/// actually needs, which is that the element is on screen and its rectangle has
/// stopped moving.
/// </para>
/// <para>
/// No sleep, and no clock. It spins on the observation itself, and gives up with
/// a message naming what it last saw rather than timing out silently.
/// </para>
/// </remarks>
internal static class UiSettle
{
    /// <summary>
    /// How long to keep observing before giving up.
    /// </summary>
    /// <remarks>
    /// <b>A time bound, not a count.</b> It was 500 observations, which is a
    /// bound on work for something bounded by time: a cold application laying
    /// itself out. 500 tight UIA reads is roughly a quarter of a second, and a
    /// packaged application starting from a cold page cache can take longer than
    /// that to lay out its first element — so the helper written to remove a
    /// flake could produce one, on the first run after a build and never on a
    /// warm re-run.
    ///
    /// This is still not a sleep and still not "usually long enough". The loop
    /// synchronises on the observation; the deadline exists only so a hang fails
    /// with a diagnosis instead of running forever.
    /// </remarks>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Blocks until the element is displayed and two consecutive readings of its
    /// bounds agree.
    /// </summary>
    /// <param name="inspector">Reads the element.</param>
    /// <param name="window">The window the element lives in.</param>
    /// <param name="elementId">The element.</param>
    internal static void UntilBoundsAreStable(
        IElementInspector inspector, nint window, string elementId)
    {
        ElementBounds previous = default;
        bool havePrevious = false;
        int observations = 0;
        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < GiveUpAfter)
        {
            observations++;

            ElementRead<bool> displayed = inspector.IsDisplayed(window, elementId);
            ElementRead<ElementBounds> bounds = inspector.ScreenBounds(window, elementId);

            if (displayed.Outcome != ElementReadOutcome.Read ||
                bounds.Outcome != ElementReadOutcome.Read)
            {
                havePrevious = false;
                continue;
            }

            // Zero-sized means the element exists but has not been laid out,
            // which reads as "stable" twice in a row if it is not excluded.
            bool laidOut = displayed.Value && bounds.Value.Width > 0 && bounds.Value.Height > 0;

            if (laidOut && havePrevious && bounds.Value == previous)
            {
                return;
            }

            previous = bounds.Value;
            havePrevious = laidOut;
        }

        Assert.Fail(
            $"Element {elementId} never settled in {elapsed.Elapsed.TotalSeconds:F1}s " +
            $"across {observations} observations. Last bounds {previous}. " +
            "Either the element is not being laid out, or something kept moving the " +
            "window — including a person using the machine, which is a permanent " +
            "hazard for tests that drive a real desktop and not a driver defect.");
    }
}

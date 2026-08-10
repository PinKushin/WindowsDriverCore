using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;

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
    /// Blocks until a locator matches at least one element.
    /// </summary>
    /// <param name="finder">The finder.</param>
    /// <param name="window">The window to search.</param>
    /// <param name="kind">What to match on.</param>
    /// <param name="value">The value to match.</param>
    /// <returns>The matching element ids.</returns>
    /// <remarks>
    /// A freshly launched application has a window before it has content, and
    /// how long that gap is depends on the machine's load — so a fixture that
    /// launches and searches in the next statement is asserting on a race. It
    /// passes on an idle machine and fails when the whole solution's test
    /// assemblies run at once, which is exactly the shape of failure that gets
    /// filed as noise.
    ///
    /// Settings is the reason this exists: far slower to populate than
    /// Calculator, and its fixture had no wait at all.
    /// </remarks>
    internal static IReadOnlyList<string> UntilSomethingMatches(
        IElementFinder finder, nint window, LocatorKind kind, string value)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        FindFailure lastFailure = FindFailure.None;

        while (elapsed.Elapsed < GiveUpAfter)
        {
            FindResult found = finder.FindAll(window, kind, value);
            lastFailure = found.Failure;

            if (found.Failure == FindFailure.None && found.ElementIds.Count > 0)
            {
                return found.ElementIds;
            }
        }

        Assert.Fail(
            $"Nothing matched {kind} '{value}' within {GiveUpAfter.TotalSeconds:F0}s " +
            $"(last failure: {lastFailure}). The application has a window but no content, " +
            "or the locator is wrong.");

        return [];
    }

    /// <summary>Spins until a condition holds, or fails the test.</summary>
    /// <param name="condition">What the caller is waiting for.</param>
    /// <param name="timeout">How long to keep observing.</param>
    /// <param name="what">Named in the failure message.</param>
    /// <remarks>
    /// Synchronise on the condition, never on the clock: a sleep long enough for
    /// a fast desktop is not long enough for a loaded one, and a sleep long
    /// enough for both wastes the difference on every run.
    /// </remarks>
    internal static void Until(
        Func<bool> condition,
        TimeSpan timeout,
        string what = "the condition")
    {
        ArgumentNullException.ThrowIfNull(condition);

        Stopwatch clock = Stopwatch.StartNew();

        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }
        }

        Assert.Fail($"Timed out after {timeout.TotalSeconds:F0}s waiting for {what}.");
    }

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

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
    private const int MaxObservations = 500;

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

        for (int observation = 0; observation < MaxObservations; observation++)
        {
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
            $"Element {elementId} never settled: last bounds {previous}. " +
            "The window is still moving, or the element is not being laid out.");
    }
}

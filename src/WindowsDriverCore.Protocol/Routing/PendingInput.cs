using System.Diagnostics;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Waits for input this session dispatched, before anything it could have
/// changed is read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifted out of <c>ElementPropertyRoutes</c> unchanged, because the race is
/// not about elements.</b> It lived there because that is where it was measured:
/// a click, then an immediate read of the element's text. The question it
/// answers is "can input this driver dispatched have changed the answer", and
/// that is equally true of a window title after a navigation, a window rectangle
/// after a drag, the page source after any interaction, a handle list after a
/// click that opened a window, and an alert's text after the click that raised
/// it.
/// </para>
/// <para>
/// <b>The owner's direction, 2026-08-30:</b> "no flake no flake no flake,
/// winappdriver doesnt flake we shouldnt and theres absolutely no reason for it
/// most of the time besides us not implementing something or diverging from
/// winappdriver in our actual behavior." A read that races IS a divergence — the
/// reference wins the same race by accident, because a single find costs it
/// ~1070 ms.
/// </para>
/// <para>
/// It costs nothing when no input is outstanding, which is the common case.
/// </para>
/// </remarks>
internal static class PendingInput
{
    /// <summary>Waits for typed input to land, if any is in flight.</summary>
    /// <param name="session">The session, whose flag is cleared once drained.</param>
    /// <param name="windows">Used to wait on the application.</param>
    /// <param name="log">Records whether the wait actually ran. Optional.</param>
    /// <remarks>
    /// <para>
    /// <b>Measured 2026-08-11, three candidate primitives, one survivor.</b>
    /// A synchronous <c>WM_NULL</c> is delivered AHEAD of queued input, so it
    /// returns at once and proves only that the thread is responsive.
    /// <c>AttachThreadInput</c> plus <c>GetQueueStatus</c> reported zero pending
    /// keys while 51 were still queued. <c>WaitForInputIdle</c> returned with all
    /// 52 characters present, five times out of five, waiting 46-195 ms.
    /// </para>
    /// <para>
    /// <b>It is not free, which is why it is here and not in <c>/keys</c>.</b>
    /// Typing stays at ~4 ms, a session that never types never waits, and the
    /// cost lands once per typing burst on the read that depends on it. For
    /// comparison, WinAppDriver spends ~2500 ms typing the same 52 characters
    /// and still races.
    /// </para>
    /// </remarks>
    internal static void Drain(
        DriverSession session, IWindowLocator windows, ITerminationLog? log)
    {
        if (!session.InputPending)
        {
            return;
        }

        // CAPTURED BEFORE CLEARING, because the floor below is measured from it.
        long? dispatchedAt = session.DispatchedAt;

        // Cleared either way. A wait that fails - no message loop, or a process
        // this driver may not open - must not make every later read retry it.
        session.InputPending = false;

        long started = Stopwatch.GetTimestamp();
        bool waited = windows.WaitForInputProcessed(session.WindowHandle);

        // AND THEN A FLOOR, BECAUSE THE WAIT ABOVE ANSWERS THE WRONG QUESTION
        // WHEN THE INPUT HAS NOT BEEN DELIVERED YET.
        //
        // WaitForInputIdle asks "is this process waiting for input". Injected
        // input sits in the SYSTEM queue for a moment before it reaches the
        // application's, and during that moment the application is idle and the
        // wait returns in under a millisecond - measured repeatedly as
        // "drain -> waited 0.8 ms" immediately before a read of the old value.
        //
        // MEASURED per-test on the guest against the reference:
        //
        //                            WinAppDriver   this driver
        //   MouseClick                     3.90 s       0.067 s   fails
        //   ClickElement                   8.17 s       0.29 s    fails
        //   GetElementDisplayedState       9.64 s       1.51 s    fails
        //
        // Those tests carry no synchronisation: they click and read. The
        // reference passes because a single find costs it ~1070 ms, so the
        // application has caught up by accident. Answering before the
        // application has reacted is reporting the wrong state, and being fast
        // is no defence.
        //
        // Measured from when the input was DISPATCHED rather than from here, so
        // a client that does other work in between pays nothing, and the floor
        // is spent once per input rather than once per read.
        if (dispatchedAt is long dispatched)
        {
            TimeSpan since = Stopwatch.GetElapsedTime(dispatched);
            TimeSpan remaining = ReactionFloor - since;

            if (remaining > TimeSpan.Zero)
            {
                Thread.Sleep(remaining);
            }
        }

        // THE RESULT IS NO LONGER DISCARDED, and that was a real defect by this
        // repository's own rule: a return-value error signal is checked before
        // reading dependent state.
        //
        // It still does not change what the route DOES - refusing the read
        // outright is worse than a racy answer, and the reference does not refuse
        // either. What changes is that a drain which never ran now says so.
        // Measured 2026-08-12: the SendKeysToElement_* family reads text 0.9 ms
        // after the keystroke that should have changed it, and nothing in the
        // transcript could distinguish "waited" from "never ran".
        //
        // Read `waited` and the elapsed time TOGETHER. True at 0 ms is a wait
        // that ran and answered instantly - a different problem from false.
        log?.InputDrained(waited, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>The least time an application gets to react before it is read.</summary>
    /// <remarks>
    /// Bounded and small on the owner's standing direction - a wait is
    /// acceptable "as long as they stay small... a second or less" and
    /// especially where it matches what a client already sees from
    /// WinAppDriver. This is far under that, and far under the reference's own
    /// incidental delay of seconds per test.
    /// </remarks>
    private static readonly TimeSpan ReactionFloor = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("WDC_REACTION_MS"), out int sweep)
            ? sweep
            : 120);
}

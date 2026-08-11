using System;
using System.Diagnostics.Tracing;
using System.IO;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Tests.Unit.Diagnostics;

/// <summary>
/// The human-readable transcript: one line per request, greppable.
/// </summary>
/// <remarks>
/// <para>
/// The format is asserted exactly rather than by "contains the route". A log is
/// read by people under time pressure and by <c>grep</c>, and both break on
/// column drift — so the layout is a contract, and a test that only checked for
/// substrings would let it move silently.
/// </para>
/// <para>
/// <b>The clock is injected.</b> <c>EventWrittenEventArgs.TimeStamp</c> is set by
/// the runtime and cannot be controlled, so a listener using it can only ever be
/// asserted loosely. A <see cref="TimeProvider"/> makes the whole line a
/// deterministic prediction.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TextRequestLogTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 11, 9, 15, 4, 250, TimeSpan.Zero);

    [Test]
    public void ARequest_IsWrittenAsOneLine()
    {
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IRequestLog)source).RequestCompleted("POST", "/session/abc/element", 404, 7, 33.42);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z POST /session/abc/element -> 404 jwp 7 33.4 ms" +
            Environment.NewLine);
    }

    [Test]
    public void ARequestWithNoEnvelope_ShowsADash_RatherThanMinusOne()
    {
        // -1 is the sentinel in the API and must never reach a reader: in a
        // transcript it looks like a status the protocol defines, and the
        // protocol has no negative statuses. "-" says "there was no envelope".
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IRequestLog)source).RequestCompleted(
                "GET", "/status", 200, IRequestLog.NoJsonWireStatus, 1.05);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z GET /status -> 200 jwp - 1.1 ms" + Environment.NewLine);
    }

    [Test]
    public void AFind_IsWrittenIndentedUnderTheRequestThatCausedIt()
    {
        // Indented, because a find happens INSIDE a request and a flat list of
        // lines loses that. The transcript is read top to bottom by a person
        // trying to see which step of a command failed.
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IFindLog)source).FindCompleted("AutomationId", "num5Button", 1, string.Empty, 12.04);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   find AutomationId='num5Button' -> 1 match(es) 12.0 ms" +
            Environment.NewLine);
    }

    [Test]
    public void AFindThatCouldNotRun_SaysSo_WhereZeroMatchesDoesNot()
    {
        // The two produce the same match count and mean opposite things. If the
        // transcript rendered them alike, every "element could not be located"
        // investigation would start in the wrong place.
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IFindLog)source).FindCompleted("Name", "absent", 0, string.Empty, 8.0);
            ((IFindLog)source).FindCompleted("Name", "absent", 0, "NoSuchWindow", 0.2);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   find Name='absent' -> 0 match(es) 8.0 ms" +
            Environment.NewLine +
            "2026-08-11T09:15:04.250Z   find Name='absent' -> 0 match(es) FAILED: NoSuchWindow 0.2 ms" +
            Environment.NewLine);
    }

    [Test]
    public void AnElementAction_NamesTheRungThatActed()
    {
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((IInteractionLog)source).ElementActionCompleted(
                "Click", "Performed", "ancestor:1/Toggle", 4.0);

            // A failure has no rung, and the line must not read "via " with
            // nothing after it.
            ((IInteractionLog)source).ElementActionCompleted(
                "Click", "NotInteractable", string.Empty, 0.5);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   Click -> Performed via ancestor:1/Toggle 4.0 ms" +
            Environment.NewLine +
            "2026-08-11T09:15:04.250Z   Click -> NotInteractable 0.5 ms" +
            Environment.NewLine);
    }

    [Test]
    public void ALaunch_CarriesItsProcessWindowAndCost()
    {
        // The cost is the informative part: the window search times out at ten
        // seconds, so a launch that took 9,600 ms and one that took 40 ms both
        // return a handle and mean entirely different things.
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((ILaunchLog)source).ApplicationLaunched(
                "Calculator", 1234, 0x00A1B2C3, "ApplicationFrameWindow", string.Empty, 9600.0);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   launch 'Calculator' -> pid 1234 window 0xA1B2C3 " +
            "(ApplicationFrameWindow) 9600.0 ms" + Environment.NewLine);
    }

    [Test]
    public void ATerminationSaysLoudlyWhenTheProcessSurvived()
    {
        // "ended" and "STILL RUNNING" rather than True/False. The false case is
        // the one that matters and it should not need decoding: a session that
        // ends while its application keeps running hands the next run a warm
        // application, which turns a cold-launch measurement into a re-attach.
        StringWriter written = new();

        using (TextRequestLogListener listener = new(written, new FixedClock(Noon)))
        using (DriverEventSource source = new())
        {
            ((ITerminationLog)source).ApplicationTerminated(1234, ended: true, 12.0);
            ((ITerminationLog)source).ApplicationTerminated(4321, ended: false, 5000.0);
        }

        written.ToString().ShouldBe(
            "2026-08-11T09:15:04.250Z   terminate pid 1234 -> ended 12.0 ms" + Environment.NewLine +
            "2026-08-11T09:15:04.250Z   terminate pid 4321 -> STILL RUNNING 5000.0 ms" +
            Environment.NewLine);
    }

    [Test]
    public void AnUnrelatedEventSource_IsNotSubscribedTo()
    {
        // THE CONTROL, and it measures SUBSCRIPTION rather than output. An
        // EventListener sees every EventSource in the process — the BCL's, and
        // any library the host loads — so a transcript polluted with those is
        // worse than useless.
        //
        // The obvious version of this test cannot fail: write a noise event and
        // assert the transcript stayed empty. The listener never enables that
        // source, so nothing is delivered, and the assertion holds whether the
        // listener filters correctly or writes everything it is given. Enabling
        // is the variable, so enabling is what gets measured.
        StringWriter written = new();

        using TextRequestLogListener listener = new(written, new FixedClock(Noon));
        using DriverEventSource ours = new();
        using SomebodyElsesEventSource other = new();

        ours.IsEnabled().ShouldBeTrue(
            "the driver's own source must be subscribed, or nothing is transcribed at all");

        other.IsEnabled().ShouldBeFalse(
            "subscribing to every source in the process would fill the transcript with " +
            "events from the BCL and from anything else loaded in the host");
    }

    /// <summary>A clock that does not move, so the line is a prediction.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Stands in for any other EventSource in the process.</summary>
    [EventSource(Name = "PinKushin-WindowsDriverCore-TestNoise")]
    private sealed class SomebodyElsesEventSource : EventSource
    {
        [Event(1, Level = EventLevel.Informational)]
        internal void SomethingHappened(string what)
        {
            if (IsEnabled())
            {
                WriteEvent(1, what);
            }
        }
    }
}

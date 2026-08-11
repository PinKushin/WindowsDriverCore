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

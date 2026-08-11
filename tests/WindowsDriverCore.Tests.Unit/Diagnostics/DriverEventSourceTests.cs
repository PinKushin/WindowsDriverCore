using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Tests.Unit.Diagnostics;

/// <summary>
/// The driver's diagnostic events actually reach a listener, carrying the values
/// they were given.
/// </summary>
/// <remarks>
/// <para>
/// <b>An EventSource fails silently by design, which is the hazard this fixture
/// exists for.</b> If the <c>[Event]</c> attribute disagrees with the method
/// signature — wrong id, wrong argument count, an unsupported parameter type —
/// the runtime does not throw at the call site. It records the problem in
/// <see cref="EventSource.ConstructionException"/>, disables the source, and
/// every <c>WriteEvent</c> afterwards returns quietly. A driver instrumented that
/// way logs nothing and reports no error, which is indistinguishable from a quiet
/// day.
/// </para>
/// <para>
/// So the well-formedness check is a separate test rather than an assertion
/// inside the behavioural one. If they were merged, a broken manifest and a
/// missing <c>WriteEvent</c> call would produce the same red, and the failure
/// message would name the wrong defect.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DriverEventSourceTests
{
    [Test]
    public void TheEventSource_IsWellFormed()
    {
        using DriverEventSource source = new();

        // Not "did it throw" — it does not throw. The exception is PARKED here
        // and the source silently stops working.
        source.ConstructionException.ShouldBeNull(
            "a malformed EventSource disables itself and every WriteEvent after it " +
            "becomes a no-op, so the driver would log nothing and say nothing");

        source.Name.ShouldBe(DriverEventSource.SourceName);
    }

    [Test]
    public void ACompletedRequest_ReachesAListener_WithItsRouteStatusAndCost()
    {
        // The listener must exist BEFORE the source: OnEventSourceCreated is how
        // a listener discovers sources, and it fires on construction.
        using CapturingListener listener = new();
        using DriverEventSource source = new();

        // Called THROUGH the interface deliberately: that is the reference the
        // route handler will hold, and an explicit implementation would compile
        // and then not be reachable that way.
        ((IRequestLog)source).RequestCompleted("POST", "/session/abc/element", 200, 0, 33.4);

        IReadOnlyList<EventWrittenEventArgs> captured = listener.WaitForOne();

        captured.Count.ShouldBe(1, "exactly one call was made");

        EventWrittenEventArgs written = captured[0];
        written.EventId.ShouldBe(DriverEventSource.RequestCompletedEventId);

        // Exact values, in order. A count-only assertion would pass for an event
        // that carried the right shape and the wrong contents — and getting the
        // argument ORDER wrong is the specific mistake WriteEvent cannot catch,
        // because every payload is just a positional list.
        object?[] payload = [.. written.Payload ?? []];
        payload.Length.ShouldBe(5);
        payload[0].ShouldBe("POST");
        payload[1].ShouldBe("/session/abc/element");
        payload[2].ShouldBe(200);
        payload[3].ShouldBe(0);
        payload[4].ShouldBe(33.4);
    }

    [Test]
    public void AFailedRequest_CarriesItsJsonWireStatus_SeparatelyFromItsHttpStatus()
    {
        // The condition where correct and broken differ. JWP reports most faults
        // as HTTP 200 with a non-zero status in the envelope, so a log that kept
        // only the HTTP code could not tell a working command from a failing one
        // — and every request in a suite run would read as a success.
        using CapturingListener listener = new();
        using DriverEventSource source = new();

        ((IRequestLog)source).RequestCompleted("POST", "/session/abc/element", 200, 7, 1.0);

        object?[] payload = [.. listener.WaitForOne()[0].Payload ?? []];

        payload[2].ShouldBe(200, "the HTTP status of a JWP fault is still 200");
        payload[3].ShouldBe(7, "and 'no such element' is only visible here");
    }

    /// <summary>Collects events from the driver's source only.</summary>
    private sealed class CapturingListener : EventListener
    {
        private readonly List<EventWrittenEventArgs> _events = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<EventWrittenEventArgs> WaitForOne()
        {
            // EventSource delivers on the writing thread for an in-process
            // listener, so this is already satisfied by the time it is called.
            // It spins on the observation rather than assuming that, because
            // "assume it has arrived" is how a diagnostic fixture becomes the
            // flakiest thing in the suite.
            SpinWait.SpinUntil(() => Count > 0, TimeSpan.FromSeconds(5))
                .ShouldBeTrue("no event reached the listener");

            lock (_gate)
            {
                return [.. _events];
            }
        }

        private int Count
        {
            get
            {
                lock (_gate)
                {
                    return _events.Count;
                }
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            ArgumentNullException.ThrowIfNull(eventSource);

            if (eventSource.Name == DriverEventSource.SourceName)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            lock (_gate)
            {
                _events.Add(eventData);
            }
        }
    }
}

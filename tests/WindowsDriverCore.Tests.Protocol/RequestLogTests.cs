using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Every request through the real pipeline leaves a record of what it was and how
/// it ended.
/// </summary>
/// <remarks>
/// <para>
/// This is the diagnostic that was missing. Chasing why the compatibility suite
/// failed took a bespoke probe per question all through 2026-08-11, because the
/// server said nothing about the requests it had answered. WinAppDriver prints
/// its own transcript, which is how its failures are readable at all.
/// </para>
/// <para>
/// <b>The JSON Wire status is not derivable from the HTTP status, and that is
/// measured rather than assumed.</b> In this driver's own fault table HTTP 404
/// covers status <c>7</c> (no such element) and status <c>9</c> (unknown command),
/// and HTTP 400 covers <c>10</c>, <c>23</c>, <c>100</c> and <c>105</c>. A log
/// keeping only the HTTP code cannot tell "the element was not there" from "that
/// route does not exist" — two completely different diagnoses that a transcript
/// exists to separate.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RequestLogTests : IDisposable
{
    private const string UnknownCommandRoute = "/definitely-not-a-command";
    private const string InvalidSessionRoute = "/session/not-a-real-session/orientation";

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task ARequest_IsRecordedWithItsMethodRouteAndHttpStatus()
    {
        using CapturingListener listener = new();

        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/status", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        LoggedRequest logged = listener.WaitFor("/status");

        logged.Method.ShouldBe("GET");
        logged.Route.ShouldBe("/status");
        logged.HttpStatus.ShouldBe(200);

        // /status is the one response with no envelope at all, so there is no
        // JSON Wire status to report and the log must say so rather than invent
        // a zero - which would read as "succeeded with status 0" and be
        // indistinguishable from a real success envelope.
        logged.JsonWireStatus.ShouldBe(IRequestLog.NoJsonWireStatus);
    }

    [Test]
    public async Task TwoFaultsSharingAnHttpStatus_AreStillDistinguishable()
    {
        // THE CONDITION WHERE CORRECT AND BROKEN DIFFER. Both of these answer
        // HTTP 404. A log that recorded only the HTTP status would render them
        // identically, so this is the input that a route-and-code-only
        // implementation cannot pass.
        using CapturingListener listener = new();

        HttpResponseMessage unknown = await _client.GetAsync(
            new Uri(UnknownCommandRoute, UriKind.Relative)).ConfigureAwait(false);
        HttpResponseMessage badSession = await _client.GetAsync(
            new Uri(InvalidSessionRoute, UriKind.Relative)).ConfigureAwait(false);

        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        badSession.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "if these stop sharing an HTTP status this test no longer discriminates");

        LoggedRequest loggedUnknown = listener.WaitFor(UnknownCommandRoute);
        LoggedRequest loggedBadSession = listener.WaitFor(InvalidSessionRoute);

        loggedUnknown.HttpStatus.ShouldBe(404);
        loggedBadSession.HttpStatus.ShouldBe(404);

        loggedUnknown.JsonWireStatus.ShouldBe(9, "unknown command");
        loggedBadSession.JsonWireStatus.ShouldBe(101, "invalid session id");
    }

    [Test]
    public async Task ARequestsCost_IsRecorded()
    {
        using CapturingListener listener = new();

        await _client.GetAsync(new Uri("/status", UriKind.Relative)).ConfigureAwait(false);

        LoggedRequest logged = listener.WaitFor("/status");

        // Not "greater than zero": a stopwatch read either side of an in-memory
        // request can legitimately round to 0.0 ms, so that assertion would be
        // measuring the machine. What must hold is that a NUMBER was recorded
        // and that it is not garbage.
        logged.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(0);
        logged.ElapsedMilliseconds.ShouldBeLessThan(
            60_000, "a /status that took a minute means the clock is wrong");
    }

    private sealed record LoggedRequest(
        string Method, string Route, int HttpStatus, int JsonWireStatus, double ElapsedMilliseconds);

    /// <summary>Collects the driver's request events.</summary>
    private sealed class CapturingListener : EventListener
    {
        private readonly List<LoggedRequest> _requests = [];
        private readonly Lock _gate = new();

        internal LoggedRequest WaitFor(string route)
        {
            LoggedRequest? found = null;

            bool arrived = SpinWait.SpinUntil(
                () =>
                {
                    lock (_gate)
                    {
                        found = _requests.Find(request =>
                            string.Equals(request.Route, route, StringComparison.Ordinal));
                    }

                    return found is not null;
                },
                TimeSpan.FromSeconds(10));

            if (!arrived || found is null)
            {
                lock (_gate)
                {
                    Assert.Fail(
                        $"No request event for '{route}'. Saw: " +
                        (_requests.Count == 0
                            ? "nothing at all — the pipeline is not emitting."
                            : string.Join(", ", _requests.ConvertAll(request => request.Route))));
                }
            }

            return found!;
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

            if (eventData.EventId != DriverEventSource.RequestCompletedEventId ||
                eventData.Payload is not { Count: 5 } payload)
            {
                return;
            }

            LoggedRequest request = new(
                (string)payload[0]!,
                (string)payload[1]!,
                (int)payload[2]!,
                (int)payload[3]!,
                (double)payload[4]!);

            lock (_gate)
            {
                _requests.Add(request);
            }
        }
    }
}

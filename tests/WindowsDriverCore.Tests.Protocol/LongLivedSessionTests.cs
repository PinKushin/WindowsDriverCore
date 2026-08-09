using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// One session, thousands of commands — the way real suites are told to do it.
/// </summary>
/// <remarks>
/// <para>
/// The Appium documentation's arrangement, and the one production suites tend to
/// follow, is a <b>single session for the whole suite</b>: one
/// <c>POST /session</c>, hundreds or thousands of commands, one
/// <c>DELETE</c> at the end. This driver's own integration fixtures do the
/// opposite — a session each — so nothing else here exercises what a session
/// accumulates over its life.
/// </para>
/// <para>
/// That matters because two pieces of state are per-session and grow: the record
/// of issued element ids, which exists to tell a stale element from an unknown
/// one, and the resolver's handle cache. Both were written down as untested
/// risks in <c>docs/LIMITATIONS.md</c>. Under the recommended arrangement they
/// are not an edge case, they are the normal case.
/// </para>
/// <para>
/// Headless: UI Automation is substituted, so this measures the protocol layer's
/// bookkeeping rather than an application.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LongLivedSessionTests : IDisposable
{
    private const string SessionId = "long-lived";
    private const nint Window = 0x9999;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementFinder _finder = null!;
    private IElementInspector _inspector = null!;

    [SetUp]
    public void StartServer()
    {
        _finder = Substitute.For<IElementFinder>();
        _inspector = Substitute.For<IElementInspector>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_finder);
                services.AddSingleton(_inspector);
            }));

        _client = _factory.CreateClient();

        _factory.Services.GetRequiredService<ISessionStore>().Add(new DriverSession(
            SessionId,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 1234,
            WindowHandle: Window));
    }

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private IElementRegistry Registry => _factory.Services.GetRequiredService<IElementRegistry>();

    private Task<HttpResponseMessage> Find(string value) =>
        _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/element", UriKind.Relative),
            new { @using = "accessibility id", value });

    private static string ElementId(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"42.19466560.4.{index}");

    [Test]
    public async Task ASessionAccumulatesOneRecordPerDistinctElement()
    {
        // The cost of a long session, stated as a number rather than a worry.
        // 2000 distinct elements is a modest suite: a few hundred tests each
        // finding a handful of controls.
        const int Elements = 2000;

        for (int index = 0; index < Elements; index++)
        {
            _finder.FindAll(Window, LocatorKind.AutomationId, $"control{index}").Returns(FindResult.Matched([ElementId(index)]));
            _finder.FindFirst(Window, LocatorKind.AutomationId, $"control{index}").Returns(FindResult.Matched([ElementId(index)]));

            (await Find($"control{index}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Registry.CountFor(SessionId).ShouldBe(
            Elements,
            "one record per distinct element issued, which is the growth LIMITATIONS describes");
    }

    [Test]
    public async Task FindingTheSameElementRepeatedly_DoesNotGrowTheRecord()
    {
        // The reassuring half, and the one that decides whether the growth
        // matters. A suite that hammers the same few controls — which is what
        // most page-object suites do — costs a constant, not one record per
        // command.
        _finder.FindAll(Window, LocatorKind.AutomationId, "num5Button").Returns(FindResult.Matched([ElementId(5)]));
        _finder.FindFirst(Window, LocatorKind.AutomationId, "num5Button").Returns(FindResult.Matched([ElementId(5)]));

        for (int attempt = 0; attempt < 2000; attempt++)
        {
            (await Find("num5Button")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Registry.CountFor(SessionId).ShouldBe(
            1, "repeated finds of one element must not accumulate");
    }

    [Test]
    public async Task FindingManyElementsAtOnce_RecordsEveryIdItHandedOut()
    {
        // POST /elements returns a whole list, and every id in it is one the
        // client may use later. Recording only the first would make every
        // element after the first report "no such element" instead of "stale"
        // once the page changed — the wrong error, on the common path.
        IReadOnlyList<string> ids = [.. Enumerable.Range(0, 500).Select(ElementId)];

        _finder.FindAll(Window, LocatorKind.AutomationId, "row").Returns(FindResult.Matched(ids));
        _finder.FindFirst(Window, LocatorKind.AutomationId, "row").Returns(FindResult.Matched(ids));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/elements", UriKind.Relative),
            new { @using = "accessibility id", value = "row" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Registry.CountFor(SessionId).ShouldBe(ids.Count);
    }

    [Test]
    public async Task StaleDetectionStillWorks_AfterThousandsOfCommands()
    {
        // The behaviour most likely to rot in a long session: the first touch of
        // a dead element must still answer 10 and the second 7, after the record
        // has grown large. A registry that degraded under size — evicting, or
        // colliding — would fail here and nowhere else.
        for (int index = 0; index < 2000; index++)
        {
            _finder.FindAll(Window, LocatorKind.AutomationId, $"control{index}").Returns(FindResult.Matched([ElementId(index)]));
            _finder.FindFirst(Window, LocatorKind.AutomationId, $"control{index}").Returns(FindResult.Matched([ElementId(index)]));

            await Find($"control{index}");
        }

        string firstIssued = ElementId(0);
        _inspector.Text(Window, firstIssued)
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NotFound));

        HttpResponseMessage first = await _client.GetAsync(
            new Uri($"/session/{SessionId}/element/{firstIssued}/text", UriKind.Relative));
        HttpResponseMessage second = await _client.GetAsync(
            new Uri($"/session/{SessionId}/element/{firstIssued}/text", UriKind.Relative));

        JsonDocument.Parse(await first.Content.ReadAsStringAsync())
            .RootElement.GetProperty("status").GetInt32()
            .ShouldBe(10, "the earliest element issued must still be known to be stale");

        JsonDocument.Parse(await second.Content.ReadAsStringAsync())
            .RootElement.GetProperty("status").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task DeletingTheSession_ReleasesEverythingItAccumulated()
    {
        // The one thing that makes the growth acceptable: it is bounded by the
        // session, not by the server's lifetime. A driver left running all day
        // across many suites must not keep the first suite's ids.
        for (int index = 0; index < 500; index++)
        {
            _finder.FindAll(Window, LocatorKind.AutomationId, $"control{index}").Returns(FindResult.Matched([ElementId(index)]));
            _finder.FindFirst(Window, LocatorKind.AutomationId, $"control{index}").Returns(FindResult.Matched([ElementId(index)]));

            await Find($"control{index}");
        }

        Registry.CountFor(SessionId).ShouldBe(500);

        await _client.DeleteAsync(new Uri($"/session/{SessionId}", UriKind.Relative));

        Registry.CountFor(SessionId).ShouldBe(0, "DELETE /session must release the record");
    }
}

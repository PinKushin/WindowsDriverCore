using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>POST /timeouts</c> understands the W3C body as well as the JSON Wire one.
/// </summary>
/// <remarks>
/// <para>
/// The two dialects disagree about the REQUEST here, not just the response:
/// </para>
/// <code>
/// JSON Wire  {"type": "implicit", "ms": 5000}
/// W3C        {"implicit": 5000}
/// </code>
/// <para>
/// <b>This is a blocker rather than a nicety.</b> A Selenium 4 client sets an
/// implicit wait on almost every session, and the JSON Wire reader answers the
/// W3C body with <c>invalid argument</c> — the type is absent, so it reads as a
/// malformed request. The suite that measures this driver is a Selenium 3 client
/// and sends the JSON Wire shape, so nothing on the scoreboard can see the gap.
/// </para>
/// <para>
/// <b>A separate fixture from <c>TimeoutsRouteTests</c> on purpose.</b> Proving
/// the wait is APPLIED needs a window that exists and a finder that misses,
/// where that fixture deliberately has neither — and changing a shared fixture's
/// arrangement changes what its other tests mean.
/// </para>
/// </remarks>
[TestFixture]
public sealed class W3CTimeoutBodyTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    /// <summary>Long enough to dwarf the 150 ms give-up floor and request overhead.</summary>
    private const int TheWait = 800;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IElementFinder _finder = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _finder = Substitute.For<IElementFinder>();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));
        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // Never matches. The measured variable is how long the driver keeps
        // LOOKING, so the answer has to stay "not yet" for the whole budget.
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([]));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_finder);
            }));

        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private Task<HttpResponseMessage> PostTimeouts(string sessionId, string json) =>
        _client.PostAsync(
            new Uri($"/session/{sessionId}/timeouts", UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>How long a find that will never match takes for this session.</summary>
    private async Task<TimeSpan> TimeAFailedFind(string sessionId)
    {
        long started = Stopwatch.GetTimestamp();

        await _client.PostAsJsonAsync(
            new Uri($"/session/{sessionId}/element", UriKind.Relative),
            new { @using = "accessibility id", value = "nothingHere" });

        return Stopwatch.GetElapsedTime(started);
    }

    [Test]
    public async Task TheW3CBody_IsAccepted()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await PostTimeouts(sessionId, $$"""{"implicit":{{TheWait}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The wait is applied, not merely acknowledged.</summary>
    /// <remarks>
    /// <b>Accepting and discarding passes every status-code assertion.</b> The
    /// existing JSON Wire tests all read the reply and none of them measure the
    /// effect, so a route that answered 200 and threw the number away would look
    /// correct there. The measurement is how long a find that can never succeed
    /// keeps trying, against a session that set no wait as the control — an
    /// absolute threshold would instead be measuring the test machine.
    /// </remarks>
    [Test]
    public async Task TheW3CBody_ActuallyAppliesTheWait()
    {
        string waiting = await NewSession();
        string control = await NewSession();

        await PostTimeouts(waiting, $$"""{"implicit":{{TheWait}}}""");

        TimeSpan withWait = await TimeAFailedFind(waiting);
        TimeSpan withoutWait = await TimeAFailedFind(control);

        (withWait - withoutWait).TotalMilliseconds.ShouldBeGreaterThan(
            400,
            $"the W3C body set {TheWait} ms and the control set nothing");
    }

    [Test]
    public async Task TheJsonWireBody_StillWorks_AndStillApplies()
    {
        // The control on the whole change. This is what the compatibility suite
        // sends, and it must behave exactly as it did before the W3C shape was
        // understood at all.
        string waiting = await NewSession();
        string control = await NewSession();

        HttpResponseMessage response = await PostTimeouts(
            waiting, $$"""{"type":"implicit","ms":{{TheWait}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        TimeSpan withWait = await TimeAFailedFind(waiting);
        TimeSpan withoutWait = await TimeAFailedFind(control);

        (withWait - withoutWait).TotalMilliseconds.ShouldBeGreaterThan(400);
    }

    [Test]
    public async Task AW3CPageLoadTimeout_IsStillUnimplemented()
    {
        // Unchanged, and deliberately: this driver has no navigation, so storing
        // a page-load timeout and never applying it would be reporting success
        // for doing nothing. The W3C spelling gets the same refusal as the JSON
        // Wire one rather than a quieter answer.
        string sessionId = await NewSession();

        HttpResponseMessage response = await PostTimeouts(sessionId, """{"pageLoad":300000}""");

        ((int)response.StatusCode).ShouldBe(501);
    }

    [Test]
    public async Task AW3CBodyCarryingAllThree_AppliesTheImplicitOne()
    {
        // Some clients send the full set in one request. Refusing the whole
        // body because it mentions pageLoad would make the implicit wait
        // unsettable for them - the refusal is for a request that asks ONLY for
        // something unsupported.
        string waiting = await NewSession();
        string control = await NewSession();

        HttpResponseMessage response = await PostTimeouts(
            waiting, $$"""{"implicit":{{TheWait}},"pageLoad":300000,"script":30000}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        TimeSpan withWait = await TimeAFailedFind(waiting);
        TimeSpan withoutWait = await TimeAFailedFind(control);

        (withWait - withoutWait).TotalMilliseconds.ShouldBeGreaterThan(400);
    }

    [Test]
    public async Task ABodyThatNamesNoTimeoutAtAll_IsStillABadParameter()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await PostTimeouts(sessionId, """{"nonsense":1}""");

        ((int)response.StatusCode).ShouldBe(400);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("status").GetInt32().ShouldBe(100);
    }
}

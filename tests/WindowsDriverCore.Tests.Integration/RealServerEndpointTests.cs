using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Every implemented endpoint, against the real server process over a real socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only test that runs WindowsDriverCore.exe.</b> Everything else
/// either drives the automation layer directly or hosts the pipeline in-process,
/// and neither exercises the executable, its argument parsing, its socket or its
/// startup. A server that crashes on launch or binds the wrong address passes
/// every other test in this repository.
/// </para>
/// <para>
/// One session for the whole fixture, closed at the end — which is also the
/// driver closing the application it started.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class RealServerEndpointTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string FindByAutomationId =
        "{\"using\":\"accessibility id\",\"value\":\"num5Button\"}";
    private const string FindSomethingAbsent =
        "{\"using\":\"accessibility id\",\"value\":\"nothingIsCalledThis\"}";

    private DriverServer _server = null!;
    private string _sessionId = null!;

    [OneTimeSetUp]
    public async Task StartServerAndSession()
    {
        DriverServer? started = DriverServer.Start();
        if (started is null)
        {
            Assert.Ignore("WindowsDriverCore.exe has not been built.");
        }

        _server = started;

        HttpResponseMessage created = await _server.Client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = CalculatorAumid } });

        if (!created.IsSuccessStatusCode)
        {
            Assert.Ignore($"Calculator is not available: {await created.Content.ReadAsStringAsync()}");
        }

        _sessionId = (await Body(created)).GetProperty("sessionId").GetString()!;
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server is null)
        {
            return;
        }

        if (_sessionId is not null)
        {
            // Ends the session, which closes Calculator. No process names.
            using HttpResponseMessage deleted = await _server.Client.DeleteAsync(
                new Uri($"/session/{_sessionId}", UriKind.Relative));
        }

        _server.Dispose();
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private Task<HttpResponseMessage> Get(string path) =>
        _server.Client.GetAsync(new Uri(path, UriKind.Relative));

    private Task<HttpResponseMessage> PostJson(string path, string json) =>
        _server.Client.PostAsync(
            new Uri(path, UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"));

    [Test]
    public async Task Status_AnswersOnTheSocketItWasToldToBind()
    {
        // Also proves the executable parsed its port argument. In-process
        // hosting never runs that code at all.
        HttpResponseMessage response = await Get("/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body(response)).GetProperty("build").GetProperty("version").GetString()
            .ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Sessions_ListsTheSessionThatWasCreated()
    {
        JsonElement body = await Body(await Get("/sessions"));

        body.GetProperty("value").EnumerateArray()
            .Any(entry => entry.GetProperty("id").GetString() == _sessionId)
            .ShouldBeTrue();
    }

    [Test]
    public async Task WindowHandle_IsHexAndParsesBackToALiveWindow()
    {
        JsonElement body = await Body(await Get($"/session/{_sessionId}/window_handle"));

        string handle = body.GetProperty("value").GetString()!;
        handle.ShouldStartWith("0x");

        // The format is only worth anything if it round-trips to something real.
        nint window = nint.Parse(handle[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        AppLifetime.WindowExists(window).ShouldBeTrue("the reported handle must name a live window");
    }

    [Test]
    public async Task Title_IsTheApplicationsTitle()
    {
        JsonElement body = await Body(await Get($"/session/{_sessionId}/title"));

        body.GetProperty("value").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task WindowSizeAndPosition_AreBothReported()
    {
        JsonElement size = (await Body(await Get($"/session/{_sessionId}/window/current/size")))
            .GetProperty("value");
        JsonElement position = (await Body(await Get($"/session/{_sessionId}/window/current/position")))
            .GetProperty("value");

        // A real window has a positive extent. Zero is the shape of a handle
        // that no longer resolves, reported as though it were a measurement.
        size.GetProperty("width").GetInt32().ShouldBeGreaterThan(0);
        size.GetProperty("height").GetInt32().ShouldBeGreaterThan(0);

        // Position can legitimately be negative on a multi-monitor desktop, so
        // this asserts shape rather than a range it has no right to expect.
        position.TryGetProperty("x", out _).ShouldBeTrue();
        position.TryGetProperty("y", out _).ShouldBeTrue();
    }

    [Test]
    public async Task Timeouts_AcceptsAnImplicitWait()
    {
        HttpResponseMessage response = await PostJson(
            $"/session/{_sessionId}/timeouts", "{\"type\":\"implicit\",\"ms\":500}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Timeouts_RefusesAPageLoadTimeoutAsPlainText()
    {
        HttpResponseMessage response = await PostJson(
            $"/session/{_sessionId}/timeouts", "{\"type\":\"page load\",\"ms\":500}");

        ((int)response.StatusCode).ShouldBe(501);
        (await response.Content.ReadAsStringAsync()).ShouldNotStartWith("{");
    }

    [Test]
    public async Task FindElement_ReturnsAnIdThatLaterCommandsAccept()
    {
        HttpResponseMessage found = await PostJson($"/session/{_sessionId}/element", FindByAutomationId);

        found.StatusCode.ShouldBe(HttpStatusCode.OK);

        string elementId = (await Body(found)).GetProperty("value").GetProperty("ELEMENT").GetString()!;

        // An id nothing else accepts is not an id. This is the contract between
        // find and every element command, over the wire rather than in process.
        HttpResponseMessage name = await Get($"/session/{_sessionId}/element/{elementId}/attribute/Name");

        name.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body(name)).GetProperty("value").GetString().ShouldBe("Five");
    }

    [Test]
    public async Task FindElements_WithNoMatch_IsAnEmptyArrayAndNotAnError()
    {
        HttpResponseMessage response = await PostJson(
            $"/session/{_sessionId}/elements", FindSomethingAbsent);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body(response)).GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task FindElement_WithNoMatch_IsNoSuchElement()
    {
        // The singular route disagrees with the plural one on purpose, and this
        // pair is the only place that difference is asserted over the wire.
        HttpResponseMessage response = await PostJson(
            $"/session/{_sessionId}/element", FindSomethingAbsent);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Body(response)).GetProperty("status").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task AnUnknownCommand_IsRefused_AndDoesNotStopTheServer()
    {
        HttpResponseMessage unknown = await Get($"/session/{_sessionId}/no/such/command");

        ((int)unknown.StatusCode).ShouldBeGreaterThanOrEqualTo(400);

        // The part that matters: it is still answering afterwards. A crash here
        // would fail every later test for reasons that look unrelated to it.
        (await Get("/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

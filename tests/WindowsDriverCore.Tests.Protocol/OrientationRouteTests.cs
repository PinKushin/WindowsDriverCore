using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Sessions;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The first session-scoped route, and therefore the first exercise of the
/// session filter every later route depends on.
///
/// <c>/orientation</c> is chosen because it needs a session but no desktop:
/// WinAppDriver answers LANDSCAPE unconditionally, which the recording confirms.
/// That isolates "is the session resolved correctly" from anything to do with
/// windows or UI automation.
/// </summary>
[TestFixture]
public sealed class OrientationRouteTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private ISessionStore _store = null!;
    private IWindowLocator _windows = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        // THE LOCATOR IS SUBSTITUTED SO THAT "LIVE" MEANS SOMETHING. These tests
        // seed a made-up window handle, so before this every session here was
        // one whose window does not exist - and the fixture asserted LANDSCAPE
        // for it, which is exactly the behaviour the suite's
        // GetOrientationError_NoSuchWindow says is wrong. The name said live and
        // nothing made it so.
        _windows = Substitute.For<IWindowLocator>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.AddSingleton(_windows)));

        _client = _factory.CreateClient();
        _store = _factory.Services.GetRequiredService<ISessionStore>();
    }

    /// <summary>
    /// Clears the store before each test. No test here asserts a count or
    /// emptiness, and the seeded ids do not collide across tests, but clearing
    /// keeps every test independent of what ran before it regardless.
    /// </summary>
    [SetUp]
    public void ArrangeDefaults()
    {
        _store.Clear();
        _windows.Exists(Arg.Any<nint>()).Returns(true);
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private void Seed(string id) =>
        _store.Add(new DriverSession(
            id,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 1234,
            WindowHandle: 0x1234));

    [Test]
    public async Task GetOrientation_WithALiveSession_ReturnsLandscape()
    {
        Seed("live");

        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/live/orientation", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("orientation").ResponseBody!);

        produced.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.RootElement.EnumerateObject().Select(p => p.Name));
        produced.RootElement.GetProperty("value").GetString()
            .ShouldBe(recorded.RootElement.GetProperty("value").GetString());
    }

    [Test]
    public async Task GetOrientation_EchoesTheRequestedSessionId()
    {
        // A session-scoped response carries the session id, and it must be the
        // one asked for. A handler that echoed a constant, or the first session
        // in the store, would satisfy the envelope assertion above.
        Seed("first");
        Seed("second");

        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/second/orientation", UriKind.Relative));

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        produced.RootElement.GetProperty("sessionId").GetString().ShouldBe("second");
    }

    /// <summary>
    /// An orphaned session's orientation is a fault, not LANDSCAPE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetOrientationError_NoSuchWindow</c> reads the orientation of a
    /// session whose window has been closed and expects
    /// <c>ErrorStrings.NoSuchWindow</c>. This driver answered 200 LANDSCAPE and
    /// the suite reported <c>Assert.Fail: Exception should have been thrown</c>.
    /// </para>
    /// <para>
    /// <b>The session still EXISTS, which is why the session filter cannot catch
    /// this.</b> Only the window is gone, so the check belongs in the handler -
    /// the same shape <c>NavigationRoutes</c> already uses, and for a milder
    /// version of the same reason: navigation refuses because a keystroke would
    /// otherwise land in someone else's application, and this refuses because
    /// answering LANDSCAPE for a window that does not exist is a claim about a
    /// window that does not exist.
    /// </para>
    /// </remarks>
    [Test]
    public async Task GetOrientation_WhenTheWindowIsGone_ReturnsNoSuchWindow()
    {
        Seed("orphaned");
        _windows.Exists(Arg.Any<nint>()).Returns(false);

        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/orphaned/orientation", UriKind.Relative));

        // 400/23 is JSON Wire's no such window, spelled out rather than read
        // from the fault the handler uses - taking the numbers from the code
        // under test would make this agree with whatever that code says.
        ((int)response.StatusCode).ShouldBe(400);

        using JsonDocument produced = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        produced.RootElement.GetProperty("status").GetInt32().ShouldBe(23);

        // The suite compares ErrorStrings.NoSuchWindow exactly, so this is an
        // equality and not a "contains".
        produced.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");
    }

    [Test]
    public async Task GetOrientation_UnknownSession_ReturnsInvalidSessionIdFault()
    {
        // The filter's job. Nothing about orientation is involved: the request
        // must be rejected before the handler runs at all.
        Seed("live");

        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/not-a-session/orientation", UriKind.Relative));

        RecordedResponse recorded = RecordedResponses.Named("error.invalidSessionId");
        ((int)response.StatusCode).ShouldBe(recorded.HttpStatus);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using JsonDocument recordedBody = JsonDocument.Parse(recorded.ResponseBody!);

        produced.RootElement.GetProperty("status").GetInt32()
            .ShouldBe(recordedBody.RootElement.GetProperty("status").GetInt32());
        produced.RootElement.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe("invalid session id");
        produced.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("No active session with ID not-a-session");
    }

    [Test]
    public async Task GetOrientation_UnknownSession_DoesNotFallThroughToUnknownCommand()
    {
        // The condition that separates "the filter rejected it" from "the route
        // did not match". Both produce a 404, so status alone cannot tell them
        // apart — 101 means the route matched and the session did not exist,
        // 9 would mean the route was never found.
        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/nope/orientation", UriKind.Relative));

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        produced.RootElement.GetProperty("status").GetInt32().ShouldBe(101);
        produced.RootElement.GetProperty("status").GetInt32().ShouldNotBe(9);
    }
}

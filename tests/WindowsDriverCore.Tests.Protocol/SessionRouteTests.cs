using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Sessions;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Session listing and teardown, driven through the real pipeline.
///
/// Creation is not here: it needs to launch an application, which belongs to a
/// later milestone. These routes are reachable without one because the store is
/// seeded directly, which is the point of it being an injected interface.
/// </summary>
[TestFixture]
public sealed class SessionRouteTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private ISessionStore _store = null!;

    [SetUp]
    public void StartServer()
    {
        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>();
        _client = _factory.CreateClient();
        _store = _factory.Services.GetRequiredService<ISessionStore>();
    }

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private void Seed(string id, string app)
    {
        DriverSession session = new(
            id,
            new Dictionary<string, string> { ["app"] = app },
            ProcessId: 1234,
            WindowHandle: 0x1234);

        _store.Add(session);
    }

    [Test]
    public async Task GetSessions_ListsEachSessionWithItsIdAndCapabilities()
    {
        Seed("session-one", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        Seed("session-two", "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App");

        HttpResponseMessage response = await _client.GetAsync(new Uri("/sessions", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("sessions.list").ResponseBody!);

        // Same envelope shape as the recording: status and value, no sessionId.
        produced.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.RootElement.EnumerateObject().Select(p => p.Name));

        JsonElement[] entries = produced.RootElement.GetProperty("value").EnumerateArray().ToArray();
        entries.Length.ShouldBe(2);

        // Each entry carries the id and the capabilities it was created with, and
        // the two sessions are distinguishable — a handler that echoed the first
        // session twice would satisfy a count-only assertion.
        entries[0].GetProperty("id").GetString().ShouldBe("session-one");
        entries[1].GetProperty("id").GetString().ShouldBe("session-two");
        entries[0].GetProperty("capabilities").GetProperty("app").GetString()
            .ShouldBe("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        entries[1].GetProperty("capabilities").GetProperty("app").GetString()
            .ShouldBe("Microsoft.WindowsAlarms_8wekyb3d8bbwe!App");
    }

    [Test]
    public async Task GetSessions_WithNoSessions_ReturnsAnEmptyArray_NotAnError()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/sessions", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        produced.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
        produced.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task DeleteSession_RemovesItAndReturnsStatusOnly()
    {
        Seed("doomed", "Calculator");

        HttpResponseMessage response = await _client.DeleteAsync(
            new Uri("/session/doomed", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("session.delete").ResponseBody!);

        // DELETE /session carries neither sessionId nor value.
        produced.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.RootElement.EnumerateObject().Select(p => p.Name));

        // The measurement that matters is the side effect, not the envelope.
        _store.Find("doomed").ShouldBeNull();
    }

    [Test]
    public async Task DeleteSession_LeavesOtherSessionsAlone()
    {
        // The bystander. Without it, "deleted the right one" and "cleared the
        // store" produce the same observation.
        Seed("doomed", "Calculator");
        Seed("survivor", "Alarms");

        await _client.DeleteAsync(new Uri("/session/doomed", UriKind.Relative));

        _store.Find("survivor").ShouldNotBeNull();
        _store.All().Count.ShouldBe(1);
    }

    [Test]
    public async Task DeleteSession_UnknownId_ReturnsInvalidSessionIdFault()
    {
        HttpResponseMessage response = await _client.DeleteAsync(
            new Uri("/session/never-existed", UriKind.Relative));

        RecordedResponse recorded = RecordedResponses.Named("error.invalidSessionId");
        using JsonDocument recordedBody = JsonDocument.Parse(recorded.ResponseBody!);

        ((int)response.StatusCode).ShouldBe(recorded.HttpStatus);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        produced.RootElement.GetProperty("status").GetInt32()
            .ShouldBe(recordedBody.RootElement.GetProperty("status").GetInt32());
        produced.RootElement.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe(recordedBody.RootElement.GetProperty("value").GetProperty("error").GetString());

        // WinAppDriver names the id it could not find. Asserting only the error
        // string would pass on a generic message.
        produced.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("No active session with ID never-existed");
    }

    [Test]
    public async Task DeleteSession_MessageNamesTheRequestedId()
    {
        // The control for the message above: a hardcoded message satisfies that
        // test and fails this one.
        HttpResponseMessage response = await _client.DeleteAsync(
            new Uri("/session/some-other-id", UriKind.Relative));

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        produced.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("No active session with ID some-other-id");
    }
}

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The first route driven end to end through the real HTTP pipeline.
///
/// <c>/status</c> is chosen deliberately: it needs no session, no application
/// and no desktop, so it isolates the pipeline itself. If this passes, routing,
/// serialization and the envelope shape are all working, and any later failure
/// is about the command rather than the plumbing.
/// </summary>
[TestFixture]
public sealed class StatusRouteTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>();
        _client = _factory.CreateClient();
    }

    /// <summary>Disposes the in-memory server. NUnit calls this after the fixture.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task GetStatus_ReturnsBuildAndOsWithoutAnEnvelope()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/status", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("status").ResponseBody!);

        // Same top-level keys as the recording, and specifically no envelope:
        // /status is the one route with no status field and no value wrapper.
        produced.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.RootElement.EnumerateObject().Select(p => p.Name));
        produced.RootElement.TryGetProperty("status", out _).ShouldBeFalse();
        produced.RootElement.TryGetProperty("value", out _).ShouldBeFalse();

        produced.RootElement.GetProperty("os").GetProperty("name").GetString().ShouldBe("windows");
        produced.RootElement.GetProperty("build").TryGetProperty("version", out _).ShouldBeTrue();
    }

    [Test]
    public async Task GetStatus_ReportsTheRealOsVersion_NotAHardcodedOne()
    {
        // Condition chosen so correct and broken differ: a driver that echoed the
        // recording verbatim would report 10.0.26200 on every machine. Asserting
        // against Environment.OSVersion makes the test fail on any host where the
        // hardcoded value is wrong, which is every host but the one recorded.
        HttpResponseMessage response = await _client.GetAsync(new Uri("/status", UriKind.Relative));

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        produced.RootElement.GetProperty("os").GetProperty("version").GetString()
            .ShouldBe(Environment.OSVersion.Version.ToString());
    }

    [Test]
    public async Task UnknownRoute_ReturnsUnknownCommandFault()
    {
        // WinAppDriver answers an unrecognised route with status 9 and a message
        // naming the method and path, not with an empty 404. Measured.
        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/abc/no_such_command", UriKind.Relative));

        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("error.unknownRoute").ResponseBody!);

        ((int)response.StatusCode).ShouldBe(RecordedResponses.Named("error.unknownRoute").HttpStatus);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        produced.RootElement.GetProperty("status").GetInt32()
            .ShouldBe(recorded.RootElement.GetProperty("status").GetInt32());
        produced.RootElement.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe(recorded.RootElement.GetProperty("value").GetProperty("error").GetString());

        // The message names the method and path, so a caller can see what it asked
        // for. Asserting only the error string would pass on a generic message.
        produced.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Command not recognized: GET: /session/abc/no_such_command");
    }

    [Test]
    public async Task UnknownRoute_MessageReflectsTheActualRequest()
    {
        // The control for the assertion above: a hardcoded message would satisfy
        // that test and fail this one, because the method and path differ.
        HttpResponseMessage response = await _client.PostAsync(
            new Uri("/session/xyz/something_else", UriKind.Relative), content: null);

        using JsonDocument produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        produced.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Command not recognized: POST: /session/xyz/something_else");
    }
}

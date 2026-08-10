using System;
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
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>POST /session/{id}/timeouts</c>, against the recorded contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>167 of 271 failures on the compatibility suite are this one route.</b>
/// Measured 2026-08-10 in the Windows 10 guest: the suite sets an implicit wait
/// immediately after creating a session, so every fixture died on
/// "Command not recognized: POST: /session/{id}/timeouts" before reaching a
/// single assertion. It is the largest single blocker this driver has.
/// </para>
/// <para>
/// Every expectation here comes from
/// <c>Recordings/winappdriver-responses.json</c>, captured from the real server,
/// not from the W3C specification — which disagrees, and which the previous
/// implementation followed to its cost.
/// </para>
/// <para>
/// <b>The two 501s are plain text, not JSON.</b> That is a WinAppDriver quirk
/// and it is deliberate here: a client parsing the body would break on a
/// helpfully-wrapped envelope.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TimeoutsRouteTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;

    [SetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(Substitute.For<IWindowLocator>());
            }));

        _client = _factory.CreateClient();
    }

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
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

        JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private Task<HttpResponseMessage> PostTimeouts(string sessionId, string json) =>
        _client.PostAsync(
            new Uri($"/session/{sessionId}/timeouts", UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));

    [Test]
    public async Task ImplicitWait_IsAccepted()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await PostTimeouts(sessionId, """{"type":"implicit","ms":1000}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        produced.GetProperty("status").GetInt32().ShouldBe(0);
        produced.GetProperty("sessionId").GetString().ShouldBe(sessionId);
    }

    [TestCase("page load", "page load timeout type is not supported")]
    [TestCase("script", "script timeout type is not supported")]
    public async Task UnsupportedTimeoutTypes_Answer501AsPlainText(string type, string expected)
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await PostTimeouts(
            sessionId, $$"""{"type":"{{type}}","ms":1000}""");

        // 501, and NOT a JSON envelope. Measured from the real server.
        ((int)response.StatusCode).ShouldBe(501);
        (await response.Content.ReadAsStringAsync())
            .ShouldBe($"Unimplemented Command: {expected}");
    }

    [Test]
    public async Task ANegativeImplicitWait_IsRejectedWithTheRecordedMessage()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await PostTimeouts(sessionId, """{"type":"implicit","ms":-1}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        JsonElement produced = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("error.timeouts.negativeMs").ResponseBody!);

        produced.GetProperty("status").GetInt32()
            .ShouldBe(recorded.RootElement.GetProperty("status").GetInt32());
        produced.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe(recorded.RootElement.GetProperty("value").GetProperty("error").GetString());

        // The message names both parameters, in that order. A client matching on
        // it is doing something unwise, but the recording is the contract.
        produced.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Bad Command Parameter: ms:-1, type:implicit");
    }

    [Test]
    public async Task TimeoutsAgainstAnUnknownSession_IsInvalidSessionId()
    {
        HttpResponseMessage response = await PostTimeouts(
            "00000000-0000-0000-0000-000000000000", """{"type":"implicit","ms":1000}""");

        // The session gate runs before the payload is looked at: an unknown
        // session is not a bad parameter.
        ((int)response.StatusCode).ShouldBe(404);
    }
}

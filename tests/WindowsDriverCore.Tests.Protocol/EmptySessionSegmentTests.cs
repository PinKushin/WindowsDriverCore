using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// A request whose session segment is empty.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last diagnosed test in the parity backlog.</b>
/// <c>MiscellaneousSessionError_StaleSessionId</c> quits a session and then
/// reads <c>Title</c>. Selenium 3.8 clears its session id on <c>Quit()</c>, so
/// the URL it builds carries an empty segment:
/// </para>
/// <code>
/// GET /session//title
/// </code>
/// <para>
/// and the test requires the message to begin <c>No active session with ID
/// title</c> — naming <b>title</b> as the session. The reference is therefore
/// not matching <c>/session/{sessionId}/title</c> with an empty id; it drops the
/// empty segment, leaving <c>/session/title</c>, and treats <c>title</c> as a
/// session that does not exist. ASP.NET Core routing does not match a required
/// parameter against an empty segment, so this reached the unknown-command
/// fallback and answered <c>404 jwp 9</c>.
/// </para>
/// <para>
/// <b>Collapsing the empty segment, not special-casing the word.</b> A route for
/// <c>/session//title</c> alone would pass this and leave every other command
/// Selenium can send after a quit — <c>/session//url</c>, <c>/session//window</c>
/// — answering the wrong thing. What the reference does is normalise the path.
/// </para>
/// </remarks>
[TestFixture]
public sealed class EmptySessionSegmentTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private ISessionStore _store = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>();
        _client = _factory.CreateClient();
        _store = _factory.Services.GetRequiredService<ISessionStore>();
    }

    [SetUp]
    public void ArrangeDefaults() => _store.Clear();

    [OneTimeTearDown]
    public void StopServer() => Dispose();

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

    private async Task<string?> MessageFrom(string path)
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("value").GetProperty("message").GetString();
    }

    /// <summary>The command after an empty session segment becomes the id.</summary>
    /// <remarks>
    /// <b>The exact string the suite asserts on.</b> It uses
    /// <c>StartsWith</c>, so a message that merely mentions the id somewhere
    /// would not do.
    /// </remarks>
    [Test]
    public async Task AnEmptySessionSegment_NamesTheNextSegmentAsTheSession() =>
        (await MessageFrom("/session//title"))
            .ShouldBe("No active session with ID title");

    /// <summary>A live session's own commands still route normally.</summary>
    /// <remarks>
    /// <para>
    /// <b>The control.</b> Collapsing runs on every request, so the risk is not
    /// that it fails to fire but that it changes a path nobody asked it to.
    /// </para>
    /// <para>
    /// <b>23 is <c>no such window</c>, and it is the whole answer.</b> The
    /// seeded session carries a handle that names nothing, so reaching the
    /// <c>window_handle</c> command produces exactly that — which proves both
    /// that the session resolved and that the third segment was still treated as
    /// a command. The three outcomes are distinct: untouched is 23, collapsed to
    /// <c>/session/live-one</c> is 0 with capabilities, and mangled into
    /// something unrouted is 9.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AWellFormedPath_IsUntouched()
    {
        Seed("live-one");

        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/live-one/window_handle", UriKind.Relative));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32()
            .ShouldBe(23, "the session must resolve and the command must still run");
    }

    /// <summary>An unknown session is reported by the get-capabilities route.</summary>
    /// <remarks>
    /// Collapsing only helps if something serves the two-segment path it
    /// produces. <c>GET /session/{sessionId}</c> is JSON Wire's
    /// retrieve-capabilities command; without it the collapsed path reaches the
    /// unknown-command fallback and the message is wrong for a second reason.
    /// </remarks>
    [Test]
    public async Task AnUnknownSession_IsNamedByTheCapabilitiesRoute() =>
        (await MessageFrom("/session/never-existed"))
            .ShouldBe("No active session with ID never-existed");

    /// <summary>An empty leading segment still reaches the command.</summary>
    /// <remarks>
    /// <b>The other shape a client produces by joining strings.</b> A base
    /// address ending in a separator concatenated with a path beginning with one
    /// gives <c>//status</c>, and that is the same defect at the front of the
    /// path rather than the middle. Asserted on <c>/sessions</c> because it
    /// needs no session, so a failure here cannot be a session lookup in
    /// disguise, and it carries the ordinary envelope.
    /// </remarks>
    /// <remarks>
    /// <b>An ABSOLUTE uri, and the first version of this test measured its own
    /// client instead.</b> A relative reference beginning <c>//</c> is a
    /// network-path reference under RFC 3986, so <c>new Uri("//sessions",
    /// Relative)</c> resolves to the HOST <c>sessions</c> with path <c>/</c>. It
    /// failed with unknown-command while never sending the path under test.
    /// </remarks>
    [Test]
    public async Task AnEmptyLeadingSegment_StillReachesTheCommand()
    {
        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"{_client.BaseAddress}/sessions"));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
    }

    /// <summary>A live session answers with its capabilities.</summary>
    /// <remarks>
    /// <b>The control for the route itself.</b> A handler that always faulted
    /// would satisfy both tests above while serving nothing — and the JSON Wire
    /// command is "retrieve the capabilities of the specified session", not
    /// "report that it is missing".
    /// </remarks>
    [Test]
    public async Task ALiveSession_AnswersWithItsCapabilities()
    {
        Seed("live-two");

        HttpResponseMessage response = await _client.GetAsync(
            new Uri("/session/live-two", UriKind.Relative));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
        body.RootElement.GetProperty("sessionId").GetString().ShouldBe("live-two");
        body.RootElement.GetProperty("value").GetProperty("app").GetString().ShouldBe("Calculator");
    }
}

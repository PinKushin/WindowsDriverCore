using System;
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
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>GET /session/{id}/source</c>.
/// </summary>
/// <remarks>
/// <para>
/// The compatibility suite's <c>Source.GetSource</c> loads the answer into an
/// <c>XmlDocument</c> and asserts <c>//Button</c> matches at least one node, so
/// the value is a document rather than a description of one, and the node names
/// are the bare control type names an XPath step uses.
/// </para>
/// <para>
/// <c>GetSourceError_NoSuchWindow</c> is the other half: an orphaned session must
/// answer "Currently selected window has been closed" rather than an empty
/// document. A driver that rendered the tree of a window that is gone would
/// answer <c>&lt;/&gt;</c> and look successful.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PageSourceRouteTests : IDisposable
{
    private const nint TheWindow = 0x1234;
    private const string ADocument = "<Window Name=\"Alarms\"><Button Name=\"Add\" /></Window>";

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IPageSourceReader _source = null!;

    /// <summary>Builds the server once. See <see cref="ArrangeDefaults"/> for per-test state.</summary>
    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _source = Substitute.For<IPageSourceReader>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_source);
            }));

        _client = _factory.CreateClient();
    }

    /// <summary>
    /// Rearms every default before each test.
    /// </summary>
    /// <remarks>
    /// <c>Source_WhenTheWindowHasGone_SaysSo</c> reconfigures both
    /// <c>_windows.Exists</c> and <c>_source.Source</c> inline. Without putting
    /// both back here, <c>Source_ReadsTheSessionsOwnWindow</c> — which relies on
    /// the window being alive to reach <c>_source.Source(TheWindow)</c> at all —
    /// would inherit a dead window from whichever test ran before it.
    /// </remarks>
    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _source.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _source.Source(Arg.Any<nint>()).Returns(ADocument);
    }

    [OneTimeTearDown]
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

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<JsonElement> GetSource(string sessionId)
    {
        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/source", UriKind.Relative));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Test]
    public async Task Source_AnswersTheProjectedDocument()
    {
        string sessionId = await NewSession();

        JsonElement body = await GetSource(sessionId);

        body.GetProperty("status").GetInt32().ShouldBe(0);
        body.GetProperty("value").GetString().ShouldBe(ADocument);
    }

    [Test]
    public async Task Source_WhenTheWindowHasGone_SaysSo()
    {
        string sessionId = await NewSession();
        _windows.Exists(Arg.Any<nint>()).Returns(false);
        _source.Source(Arg.Any<nint>()).Returns((string?)null);

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/source", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        JsonElement body = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync()).RootElement;

        body.GetProperty("status").GetInt32().ShouldBe(23);
        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");
    }

    [Test]
    public async Task Source_ReadsTheSessionsOwnWindow()
    {
        // The control. A reader handed the wrong window would answer a perfectly
        // well-formed document of somebody else's tree, and every assertion above
        // would still pass.
        string sessionId = await NewSession();

        await GetSource(sessionId);

        _source.Received(1).Source(TheWindow);
    }
}

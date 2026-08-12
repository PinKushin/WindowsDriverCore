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
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>GET /session/{id}/element/{id}/equals/{other}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth five tests on the compatibility suite</b>, and the route was
/// entirely absent — measured by diffing our run against WinAppDriver's by test
/// name rather than inferred: <c>CompareElements</c>,
/// <c>CompareElementsError_NoSuchElement</c>, <c>_NoSuchWindow</c>,
/// <c>_StaleElement</c> and <c>_StaleElementParameter</c> all pass through
/// WinAppDriver and all failed here.
/// </para>
/// <para>
/// <b>Comparing the id strings IS comparing the elements.</b> An element id in
/// this driver is the UIA RuntimeId — <c>UiaElementFinder</c> mints it from
/// <c>ReadRuntimeIds</c> — and a RuntimeId is unique per element and stable
/// while it lives. So two references to one element carry identical strings and
/// two different elements never do. This is exact, not an approximation, and it
/// avoids a UIA round trip purely to re-derive what the id already says.
/// </para>
/// <para>
/// <b>Both ids are probed, and the fault names whichever one failed.</b> The
/// suite distinguishes a bad FIRST element from a bad SECOND one
/// (<c>_NoSuchElement</c> against <c>_StaleElementParameter</c>), and the
/// stale-versus-unknown answer depends on which id the registry issued — so
/// asking about the wrong id would report the wrong fault.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ElementEqualsRouteTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IElementInspector _inspector = null!;
    private IElementRegistry _registry = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _inspector = Substitute.For<IElementInspector>();
        _registry = Substitute.For<IElementRegistry>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_inspector);
                services.AddSingleton(_registry);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _inspector.ClearReceivedCalls();
        _registry.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // Every id resolves unless a test says otherwise.
        _inspector.TagName(Arg.Any<nint>(), Arg.Any<string>())
            .Returns(ElementRead.Success("Button"));
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

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

    private async Task<HttpResponseMessage> Equals(string sessionId, string left, string right) =>
        await _client.GetAsync(
            new Uri($"/session/{sessionId}/element/{left}/equals/{right}", UriKind.Relative));

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Test]
    public async Task TheSameElement_ComparesEqual()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await Equals(sessionId, "42.7.1", "42.7.1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(response)).GetProperty("value").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task TwoDifferentElements_CompareUnequal()
    {
        // The control. Without it, "always true" passes the test above - and
        // CompareElements asserts the NEGATIVE case, comparing the title bar
        // against a reference element and requiring them to differ.
        string sessionId = await NewSession();

        HttpResponseMessage response = await Equals(sessionId, "42.7.1", "42.9.4");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(response)).GetProperty("value").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task AnUnknownFirstElement_IsNoSuchElement()
    {
        string sessionId = await NewSession();

        _inspector.TagName(TheWindow, "42.7.1")
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NotFound));
        _registry.TryConsume(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        HttpResponseMessage response = await Equals(sessionId, "42.7.1", "42.9.4");

        (await BodyOf(response)).GetProperty("status").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task AStaleSecondElement_IsReportedAgainstTheSecondId()
    {
        // CompareElementsError_StaleElementParameter: the FIRST element is fine
        // and the second is dead. Asking the registry about the first id would
        // answer for the wrong element and could turn a stale parameter into
        // "no such element".
        string sessionId = await NewSession();

        _inspector.TagName(TheWindow, "42.9.4")
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NotFound));
        _registry.TryConsume(Arg.Any<string>(), "42.9.4").Returns(true);

        HttpResponseMessage response = await Equals(sessionId, "42.7.1", "42.9.4");

        (await BodyOf(response)).GetProperty("status").GetInt32().ShouldBe(10);
        _registry.Received().TryConsume(Arg.Any<string>(), "42.9.4");
    }

    [Test]
    public async Task AClosedWindow_OutranksTheComparison()
    {
        string sessionId = await NewSession();

        _windows.Exists(Arg.Any<nint>()).Returns(false);
        _inspector.TagName(Arg.Any<nint>(), Arg.Any<string>())
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NoSuchWindow));

        HttpResponseMessage response = await Equals(sessionId, "42.7.1", "42.9.4");

        (await BodyOf(response)).GetProperty("status").GetInt32().ShouldBe(23);
    }
}

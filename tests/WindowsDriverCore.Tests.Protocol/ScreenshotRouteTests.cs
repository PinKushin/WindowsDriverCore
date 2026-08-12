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
/// <c>GET /screenshot</c> and <c>GET /element/{id}/screenshot</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth five tests on the compatibility suite</b>, and the route was
/// entirely absent before this — not a wrong answer, no answer.
/// </para>
/// <para>
/// <b>The suite asserts the SIZE, not just that bytes came back.</b>
/// <c>GetElementScreenshot</c> compares the decoded image's height and width
/// against the element's own reported size, and <c>GetScreenshot</c> does the
/// same against the window. So the rectangle handed to the capture is the
/// measured variable here, not the base64 round trip.
/// </para>
/// <para>
/// <b><see cref="IScreenCapture"/> is substituted deliberately.</b>
/// <c>WebApplicationFactory</c> boots the real container, so leaving the live
/// implementation in place would photograph the developer's actual desktop
/// during a unit-test run.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ScreenshotRouteTests : IDisposable
{
    private const nint TheWindow = 0x1234;
    private static readonly byte[] ThePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IElementInspector _inspector = null!;
    private IScreenCapture _capture = null!;
    private IElementRegistry _registry = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _inspector = Substitute.For<IElementInspector>();
        _capture = Substitute.For<IScreenCapture>();
        _registry = Substitute.For<IElementRegistry>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_inspector);
                services.AddSingleton(_capture);
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
        _capture.ClearReceivedCalls();
        _registry.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(100, 200, 800, 600));

        _capture.CapturePng(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ThePng);
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

    [Test]
    public async Task TheSessionScreenshot_CapturesTheWindowsOwnRectangle()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/screenshot", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The exact rectangle, not "some rectangle". The suite compares the
        // decoded image's dimensions against the window's reported size, so a
        // capture of the whole screen would pass a "bytes came back" assertion
        // and fail the suite.
        _capture.Received(1).CapturePng(100, 200, 800, 600);
    }

    [Test]
    public async Task TheSessionScreenshot_IsReturnedAsBase64()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/screenshot", UriKind.Relative));

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // JWP carries the image as a base64 string in "value" - the client
        // decodes it straight back to the PNG bytes.
        body.RootElement.GetProperty("value").GetString()
            .ShouldBe(Convert.ToBase64String(ThePng));
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task TheWindowIsBroughtToTheForeground_BeforeItIsCaptured()
    {
        // The suite states this outright: it maximizes Notepad over the top of
        // Alarms & Clock and then expects the Alarms screenshot to show Alarms.
        // A blit of an obscured window returns whatever is covering it.
        string sessionId = await NewSession();

        await _client.GetAsync(new Uri($"/session/{sessionId}/screenshot", UriKind.Relative));

        _windows.Received().BringToForeground(TheWindow);
    }

    [Test]
    public async Task WhenTheWindowIsGone_TheSessionScreenshotSaysSo()
    {
        string sessionId = await NewSession();

        _windows.Exists(TheWindow).Returns(false);
        _windows.GetBounds(TheWindow).Returns((WindowBounds?)null);

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/screenshot", UriKind.Relative));

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(23);

        // And nothing was photographed. Capturing a rectangle belonging to a
        // window that no longer exists would grab whatever now occupies that
        // screen space - another application's contents.
        _capture.DidNotReceive().CapturePng(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Test]
    public async Task TheElementScreenshot_CapturesTheElementsOwnRectangle()
    {
        string sessionId = await NewSession();

        _inspector.ScreenBounds(TheWindow, "42")
            .Returns(ElementRead.Success(new ElementBounds(310, 420, 50, 24)));

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/screenshot/42", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The element's rectangle, not the window's. The suite asserts the
        // element capture is strictly SMALLER than the window capture, so
        // falling back to the window would be caught there and nowhere else.
        _capture.Received(1).CapturePng(310, 420, 50, 24);
    }

    /// <summary>The W3C spelling of the element screenshot is served too.</summary>
    /// <remarks>
    /// <b>Nothing else tests this.</b> The compatibility suite is a Selenium 2
    /// client and asks for <c>/session/{id}/screenshot/{elementId}</c>; it will
    /// never request the W3C path, so a break there would ship unnoticed. This
    /// driver serves both deliberately - JWP is the contract and W3C is
    /// additive - and "we serve it" is only true if something checks.
    /// </remarks>
    [Test]
    public async Task TheW3CElementScreenshotPath_IsServedAsWell()
    {
        string sessionId = await NewSession();

        _inspector.ScreenBounds(TheWindow, "42")
            .Returns(ElementRead.Success(new ElementBounds(310, 420, 50, 24)));

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/element/42/screenshot", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _capture.Received(1).CapturePng(310, 420, 50, 24);
    }

    [Test]
    public async Task AStaleElement_IsReportedAsStale_NotPhotographed()
    {
        string sessionId = await NewSession();

        // NotFound plus a registry that confirms it issued the id IS staleness -
        // the inspector cannot tell a dead element from an id we never handed
        // out, so that distinction is drawn in the protocol layer.
        _inspector.ScreenBounds(TheWindow, "42")
            .Returns(ElementRead.Failed<ElementBounds>(ElementReadOutcome.NotFound));
        _registry.TryConsume(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/screenshot/42", UriKind.Relative));

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(10);

        _capture.DidNotReceive().CapturePng(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>());
    }
}

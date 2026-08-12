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
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>POST /session/{id}/back</c> and <c>POST /session/{id}/forward</c>.
/// </summary>
/// <remarks>
/// Worth five suite tests, and both were absent — <c>/back</c> answered
/// <c>404 jwp 9</c> four times in the last measured run and <c>/forward</c> once.
/// </remarks>
[TestFixture]
public sealed class NavigationRouteTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    /// <summary>Selenium's key codes, which are what the keyboard layer speaks.</summary>
    private const string LeftArrow = "";
    private const string RightArrow = "";
    private const char Alt = '';

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IKeyboardInput _keyboard = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _keyboard = Substitute.For<IKeyboardInput>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_keyboard);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _keyboard.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _keyboard.Type(Arg.Any<string>(), Arg.Any<HeldModifiers>()).Returns(true);
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

    private async Task<HttpResponseMessage> Navigate(string sessionId, string direction) =>
        await _client.PostAsync(new Uri($"/session/{sessionId}/{direction}", UriKind.Relative), null);

    [Test]
    public async Task Back_SendsAltAndTheLeftArrow()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await Navigate(sessionId, "back");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The exact gesture, not "some keystroke". Alt+Left is what raises
        // BackRequested in a packaged application; a bare Left arrow moves the
        // caret and would leave the view exactly where it was.
        _keyboard.Received(1).Type(
            LeftArrow, Arg.Is<HeldModifiers>(held => held.Contains(Alt)));
    }

    [Test]
    public async Task Forward_SendsAltAndTheRightArrow()
    {
        // The control: without it, "always send Left" passes the test above and
        // makes forward navigate backwards.
        string sessionId = await NewSession();

        await Navigate(sessionId, "forward");

        _keyboard.Received(1).Type(
            RightArrow, Arg.Is<HeldModifiers>(held => held.Contains(Alt)));
    }

    [Test]
    public async Task TheModifierIsReleased_SoLaterKeystrokesAreNotAltKeystrokes()
    {
        // A modifier left down outlives the request and turns every subsequent
        // keystroke in the run into Alt+key. This driver has been bitten by
        // modifier persistence before.
        string sessionId = await NewSession();

        await Navigate(sessionId, "back");

        _keyboard.Received().ReleaseHeld(Arg.Any<HeldModifiers>());
    }

    [Test]
    public async Task TheWindowIsRaised_BeforeTheKeystroke()
    {
        // Synthesized keys go to the FOREGROUND window, not to a handle. Without
        // raising it first the gesture lands wherever the user last clicked.
        string sessionId = await NewSession();

        await Navigate(sessionId, "back");

        _windows.Received().BringToForeground(TheWindow);
    }

    [Test]
    public async Task WhenTheWindowIsGone_NothingIsTyped()
    {
        // NavigateBackError_NoSuchWindow uses an orphaned session. Beyond the
        // message, the keystroke must not be dispatched at all: it would land in
        // whatever application now holds the foreground.
        string sessionId = await NewSession();

        _windows.Exists(TheWindow).Returns(false);

        HttpResponseMessage response = await Navigate(sessionId, "back");

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(23);
        body.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");

        _keyboard.DidNotReceive().Type(Arg.Any<string>(), Arg.Any<HeldModifiers>());
    }
}

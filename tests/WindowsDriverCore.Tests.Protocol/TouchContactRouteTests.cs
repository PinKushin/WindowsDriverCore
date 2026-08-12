using System;
using System.Collections.Generic;
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

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>POST /touch/down</c>, <c>/touch/move</c> and <c>/touch/up</c>.
/// </summary>
/// <remarks>
/// Worth two suite tests, and absent — <c>/touch/down</c> answered
/// <c>404 jwp 9</c> twice in the last measured run.
/// </remarks>
[TestFixture]
public sealed class TouchContactRouteTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private ISyntheticPointer _injector = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _injector = Substitute.For<ISyntheticPointer>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_injector);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _injector.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // The window sits at (100,200), so a window-relative (30,40) is screen
        // (130,240). Non-zero deliberately: an origin of (0,0) would make
        // window-relative and screen coordinates identical and the test blind
        // to the conversion it exists to check.
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(100, 200, 800, 600));
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);

        _injector.CanInject(Arg.Any<SyntheticPointerKind>()).Returns(true);
        _injector.Inject(Arg.Any<IReadOnlyList<SyntheticContact>>()).Returns(true);
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

    private async Task<HttpResponseMessage> Touch(string sessionId, string phase, int x, int y) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/touch/{phase}", UriKind.Relative),
            new StringContent($$"""{"x":{{x}},"y":{{y}}}""", Encoding.UTF8, "application/json"));

    [Test]
    public async Task Down_InjectsADownContact_AtScreenCoordinates()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await Touch(sessionId, "down", 30, 40);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        _injector.Received(1).Inject(Arg.Is<IReadOnlyList<SyntheticContact>>(
            contacts => contacts[0].Phase == SyntheticContactPhase.Down &&
                        contacts[0].X == 130 &&
                        contacts[0].Y == 240));
    }

    [Test]
    public async Task Up_InjectsAnUpContact_NotADownOne()
    {
        // The control for the phase. Without it, "always inject Down" passes the
        // test above and a tap never lifts.
        string sessionId = await NewSession();

        await Touch(sessionId, "up", 30, 40);

        _injector.Received(1).Inject(Arg.Is<IReadOnlyList<SyntheticContact>>(
            contacts => contacts[0].Phase == SyntheticContactPhase.Up));
    }

    [Test]
    public async Task Move_InjectsAnUpdate()
    {
        string sessionId = await NewSession();

        await Touch(sessionId, "move", 30, 40);

        _injector.Received(1).Inject(Arg.Is<IReadOnlyList<SyntheticContact>>(
            contacts => contacts[0].Phase == SyntheticContactPhase.Update));
    }

    [Test]
    public async Task APointTheWindowDoesNotOwn_IsRefusedOnDown()
    {
        // An unguarded contact dispatches real touch input into whatever
        // application occupies that screen position - the worst failure this
        // driver has, because the damage lands in somebody else's window.
        string sessionId = await NewSession();

        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(false);

        HttpResponseMessage response = await Touch(sessionId, "down", 30, 40);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        _injector.DidNotReceive().Inject(Arg.Any<IReadOnlyList<SyntheticContact>>());
    }

    [Test]
    public async Task AMoveIsNotGuarded_SoADragMayLeaveTheWindow()
    {
        // Deliberate, and it matches the existing pointer path: a move follows a
        // press that was already checked, and refusing each frame would turn a
        // drag that crosses an edge into a mid-gesture failure the caller cannot
        // act on.
        string sessionId = await NewSession();

        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(false);

        HttpResponseMessage response = await Touch(sessionId, "move", 30, 40);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _injector.Received(1).Inject(Arg.Any<IReadOnlyList<SyntheticContact>>());
    }
}

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
/// <c>POST /session/{id}/appium/app/close</c>.
/// </summary>
/// <remarks>
/// Worth three suite tests, and previously absent — it answered
/// <c>404 jwp 9</c> three times in the last measured run.
/// </remarks>
[TestFixture]
public sealed class AppLifecycleRouteTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IApplicationTerminator _terminator = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _terminator = Substitute.For<IApplicationTerminator>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_terminator);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _terminator.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.Close(Arg.Any<nint>()).Returns(true);
        _windows.WaitUntilGone(Arg.Any<nint>()).Returns(true);
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

    private async Task<HttpResponseMessage> CloseApp(string sessionId) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/appium/app/close", UriKind.Relative), null);

    [Test]
    public async Task ClosingTheApp_ClosesTheWindow_AndKeepsTheSession()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await CloseApp(sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _windows.Received().Close(TheWindow);

        // The session survives: CloseApplication keeps using the id afterwards,
        // so this must not behave like DELETE /session.
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("sessionId").GetString().ShouldBe(sessionId);
    }

    [Test]
    public async Task ClosingTheApp_WaitsForTheWindowToActuallyGo()
    {
        // WM_CLOSE is posted, so the window is still alive when Close returns.
        // The suite reads WindowHandles immediately afterwards and requires it
        // empty - a race the client loses unless the wait happens server-side.
        string sessionId = await NewSession();

        await CloseApp(sessionId);

        _windows.Received().WaitUntilGone(TheWindow);
    }

    [Test]
    public async Task ClosingAnAlreadyClosedApp_IsAFault()
    {
        // The second half of CloseApplication: it calls CloseApp again and
        // requires "Currently selected window has been closed". Answering
        // success for doing nothing would pass the first close and fail here.
        string sessionId = await NewSession();

        _windows.Exists(TheWindow).Returns(false);

        HttpResponseMessage response = await CloseApp(sessionId);

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(23);
        body.RootElement.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");

        _windows.DidNotReceive().Close(Arg.Any<nint>());
    }

    [Test]
    public async Task TheProcessIsNotTerminated_OnlyTheWindowIsClosed()
    {
        // A single-instance application adds a window without adding a process,
        // so terminating by process id takes down windows a person is using.
        // WinAppDriver does exactly that on Windows 11; it is a defect worth
        // not reproducing.
        string sessionId = await NewSession();

        await CloseApp(sessionId);

        _terminator.ReceivedCalls().ShouldBeEmpty();
    }
}

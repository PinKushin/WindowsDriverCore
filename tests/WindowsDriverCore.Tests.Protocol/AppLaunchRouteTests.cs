using System;
using System.Collections.Generic;
using System.Linq;
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
/// <c>POST /appium/app/launch</c>, and the window list it needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth three suite tests</b>, and previously absent — it answered
/// <c>404 jwp 9</c> three times in a measured run.
/// </para>
/// <para>
/// <b>This is why a session needed more than one window handle.</b>
/// <c>Launch_ModernApp</c> asserts the session id is unchanged, the handle
/// COUNT has risen by one, and the current handle is different. A single
/// mutable handle can satisfy the last of those and neither of the first two.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AppLaunchRouteTests : IDisposable
{
    private const nint FirstWindow = 0x1111;
    private const nint SecondWindow = 0x2222;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, FirstWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // A REAL desktop handle, and it is load-bearing. Left unstubbed this
        // answers 0, the desktop session's window list is empty for that reason
        // alone, and ADesktopSession_ReportsNoWindowsAtAll passes whether or not
        // the desktop rule exists - which is exactly what a mutation removing
        // the rule proved.
        _windows.DesktopWindow.Returns((nint)0x9999);
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<string> NewSession(string app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app } });

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<HttpResponseMessage> Launch(string sessionId) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/appium/app/launch", UriKind.Relative), null);

    private async Task<IReadOnlyList<string>> HandlesOf(string sessionId)
    {
        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/window_handles", UriKind.Relative));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("value").EnumerateArray()
            .Select(handle => handle.GetString()!).ToList();
    }

    private async Task<string?> CurrentHandleOf(string sessionId)
    {
        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/window_handle", UriKind.Relative));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("value").GetString();
    }

    [Test]
    public async Task Relaunching_AddsAWindow_RatherThanReplacingOne()
    {
        string sessionId = await NewSession();
        (await HandlesOf(sessionId)).Count.ShouldBe(1);

        // The relaunch produces a DIFFERENT window, as a second instance does.
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, SecondWindow)));

        HttpResponseMessage response = await Launch(sessionId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Launch_ModernApp asserts exactly this: one more than before.
        (await HandlesOf(sessionId)).Count.ShouldBe(2);
    }

    [Test]
    public async Task Relaunching_PointsTheSessionAtTheNewWindow()
    {
        string sessionId = await NewSession();
        string? before = await CurrentHandleOf(sessionId);

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, SecondWindow)));

        await Launch(sessionId);

        // AreNotEqual in the suite: the session follows the window it just made.
        (await CurrentHandleOf(sessionId)).ShouldNotBe(before);
    }

    [Test]
    public async Task Relaunching_KeepsTheSameSession()
    {
        // The whole point of the route: LaunchApp is not CreateSession.
        string sessionId = await NewSession();

        HttpResponseMessage response = await Launch(sessionId);

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("sessionId").GetString().ShouldBe(sessionId);
    }

    [Test]
    public async Task Relaunching_ReplaysTheCapabilitiesThatCreatedTheSession()
    {
        // The suite sends an EMPTY body, so the application to launch can only
        // come from the session's own record of what created it.
        string sessionId = await NewSession("C:\\Windows\\System32\\notepad.exe");

        await Launch(sessionId);

        _launcher.Received().Launch(Arg.Is<ApplicationTarget>(
            target => target.App == "C:\\Windows\\System32\\notepad.exe"));
    }

    [Test]
    public async Task AClosedWindow_DropsOutOfTheHandleList()
    {
        // Membership is not liveness. The list keeps what the session opened;
        // the route reports what still exists, or a closed window would be
        // handed to a client as switchable.
        string sessionId = await NewSession();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, SecondWindow)));
        await Launch(sessionId);
        (await HandlesOf(sessionId)).Count.ShouldBe(2);

        _windows.Exists(FirstWindow).Returns(false);

        (await HandlesOf(sessionId)).Count.ShouldBe(1);
    }

    [Test]
    public async Task ADesktopSession_ReportsNoWindowsAtAll()
    {
        // GetWindowHandles_Desktop asserts exactly zero. A desktop session is
        // rooted at the desktop window, but that is not a window it owns - it
        // cannot close, move or switch away from it, and reporting it would
        // offer the client a handle it can do nothing with.
        string sessionId = await NewSession("Root");

        // The desktop window EXISTS and is the session's root, so without the
        // rule it would be reported as an owned window.
        (await CurrentHandleOf(sessionId)).ShouldNotBeNullOrEmpty(
            "the session really is rooted at a live desktop window");

        (await HandlesOf(sessionId)).ShouldBeEmpty();
    }
}

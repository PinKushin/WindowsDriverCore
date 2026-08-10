using System;
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
/// A session re-points itself when the window it holds is destroyed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> A packaged application's <c>Windows.UI.Core.CoreWindow</c>
/// is top-level and its own root when a session is created, and is later
/// destroyed — not reparented — as the application is rehosted into its
/// <c>ApplicationFrameWindow</c>. The session went on holding the dead handle, so
/// every command answered "Currently selected window has been closed". In the
/// Windows 10 guest that killed every <c>ActionsError_*</c> test at
/// <c>TestInit</c>, starting with the first test of the run.
/// </para>
/// <para>
/// It cannot be fixed at attach time: the frame does not exist yet at that
/// instant, and three attempts to prefer or wait for it returned window 0.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SessionWindowReResolveTests : IDisposable
{
    private const nint OriginalWindow = 0x1234;
    private const nint ReplacementWindow = 0x5678;
    private const int ApplicationProcess = 4242;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(
                new LaunchedApplication(ApplicationProcess, OriginalWindow)));

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_windows);
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

    /// <summary>Any session-scoped request, to drive the filter.</summary>
    private async Task Touch(string sessionId) =>
        await _client.GetAsync(new Uri($"/session/{sessionId}/window_handle", UriKind.Relative));

    private async Task<string?> ReportedWindowHandle(string sessionId)
    {
        HttpResponseMessage response =
            await _client.GetAsync(new Uri($"/session/{sessionId}/window_handle", UriKind.Relative));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("value").GetString();
    }

    [Test]
    public async Task WhenTheWindowIsDestroyed_TheSessionRePointsAtTheApplicationsCurrentWindow()
    {
        string sessionId = await NewSession();

        // The window dies, exactly as a CoreWindow does when the application is
        // rehosted, and the application now presents a different one.
        _windows.Exists(OriginalWindow).Returns(false);
        _windows.Exists(ReplacementWindow).Returns(true);
        _windows.FindMainWindow(ApplicationProcess).Returns(ReplacementWindow);

        (await ReportedWindowHandle(sessionId)).ShouldBe("0x" + ReplacementWindow.ToString("X8"));
    }

    [Test]
    public async Task WhileTheWindowIsAlive_ItIsNeverSecondGuessed()
    {
        // The control that keeps this from being a retargeting bug. A client that
        // switched windows deliberately must keep the window it asked for, so a
        // live handle is never re-resolved.
        string sessionId = await NewSession();

        _windows.FindMainWindow(Arg.Any<int>()).Returns(ReplacementWindow);

        await Touch(sessionId);

        _windows.DidNotReceive().FindMainWindow(Arg.Any<int>());
        (await ReportedWindowHandle(sessionId)).ShouldBe("0x" + OriginalWindow.ToString("X8"));
    }

    [Test]
    public async Task WhenTheWindowIsDeadAndNothingReplacesIt_TheRequestStillFails()
    {
        // A re-resolve that finds nothing must not invent a window. The route
        // then reports the window is gone — which is true — rather than silently
        // driving something else. Asserting the FAULT rather than a handle,
        // because answering a handle here would be the bug.
        string sessionId = await NewSession();

        _windows.Exists(OriginalWindow).Returns(false);
        _windows.FindMainWindow(ApplicationProcess).Returns(0);

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/window_handle", UriKind.Relative));

        response.IsSuccessStatusCode.ShouldBeFalse(
            "a session whose window is gone and cannot be replaced must say so");

        // And it must have actually tried, or this passes for the wrong reason:
        // a driver that never re-resolved at all would also fail this request.
        _windows.Received(1).FindMainWindow(ApplicationProcess);
    }
}

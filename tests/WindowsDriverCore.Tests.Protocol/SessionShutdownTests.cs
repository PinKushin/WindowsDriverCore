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
/// <c>DELETE /session</c> closing the application it started — and only that one.
/// </summary>
/// <remarks>
/// <para>
/// <b>27 tests on the compatibility suite trace back to this.</b> Measured
/// 2026-08-10 in the Windows 10 guest: the suite keeps a <c>static</c> session
/// across every test class sharing a base, so once one test deliberately closes
/// a window, every later class inherits a session pointing at a dead handle and
/// dies in <c>TestInit</c>. WinAppDriver does not have the problem because
/// <c>session.Quit()</c> closes the application and the next <c>Setup()</c>
/// starts from nothing.
/// </para>
/// <para>
/// <b>The dangerous half is what must NOT be killed.</b> A desktop session
/// addresses explorer, and an attached session (<c>appTopLevelWindow</c>)
/// addresses a window somebody else opened — terminating either would close an
/// application this driver never started. Ownership is recorded at creation
/// rather than inferred from the process id, because an attached session has a
/// perfectly real one.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SessionShutdownTests : IDisposable
{
    private const int LaunchedProcess = 4242;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationTerminator _terminator = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(LaunchedProcess, 0x1234)));

        _terminator = Substitute.For<IApplicationTerminator>();

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.DesktopWindow.Returns(0x1000);
        // An attached session resolves a REAL process that this driver did not
        // start. This is the number that must never be terminated.
        _windows.GetOwningProcessId(Arg.Any<nint>()).Returns(9999);

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_terminator);
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

    private async Task<string> NewSession(object capabilities)
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = capabilities });

        JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private Task<HttpResponseMessage> Delete(string sessionId) =>
        _client.DeleteAsync(new Uri($"/session/{sessionId}", UriKind.Relative));

    [Test]
    public async Task DeletingALaunchedSession_ClosesTheApplication()
    {
        string sessionId = await NewSession(
            new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" });

        await Delete(sessionId);

        _terminator.Received(1).Terminate(LaunchedProcess);
    }

    [Test]
    public async Task DeletingAnAttachedSession_LeavesTheApplicationAlone()
    {
        // The control that matters most. An attached session has a real process
        // id, so "terminate whatever process the session names" passes the test
        // above and closes an application belonging to someone else.
        string sessionId = await NewSession(// No 0x prefix: NumberStyles.HexNumber rejects one.
            new { appTopLevelWindow = "1234" });

        await Delete(sessionId);

        _terminator.DidNotReceive().Terminate(Arg.Any<int>());
    }

    [Test]
    public async Task DeletingTheDesktopSession_DoesNotKillExplorer()
    {
        string sessionId = await NewSession(new { app = "Root" });

        await Delete(sessionId);

        _terminator.DidNotReceive().Terminate(Arg.Any<int>());
    }

    [Test]
    public async Task WhenTwoSessionsShareOneProcess_DeletingTheFirstLeavesItRunning()
    {
        // Windows 10's Calculator is single-instance, so two sessions for the
        // same application address the SAME process. Closing it when the first
        // session ends takes the application out from under the second — which
        // is what the compatibility suite does, and why session shutdown has
        // cost 4 tests since it was added.
        string first = await NewSession(
            new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" });
        string second = await NewSession(
            new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" });

        await Delete(first);

        _terminator.DidNotReceive().Terminate(Arg.Any<int>());

        // And the last one out does close it, or the application leaks instead.
        await Delete(second);

        _terminator.Received(1).Terminate(LaunchedProcess);
    }

    [Test]
    public async Task TheSessionIsStillRemoved_EvenIfTerminationFails()
    {
        // A process that will not die is not a reason to keep the session. The
        // client asked for it to be gone and a second delete must report it
        // unknown, not hand back a session addressing a half-dead application.
        _terminator.Terminate(Arg.Any<int>()).Returns(false);

        string sessionId = await NewSession(
            new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" });

        (await Delete(sessionId)).IsSuccessStatusCode.ShouldBeTrue();

        HttpResponseMessage second = await Delete(sessionId);
        ((int)second.StatusCode).ShouldBe(404);
    }
}

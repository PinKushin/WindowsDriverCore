using System;
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
using WindowsDriverCore.Protocol.Sessions;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Session creation, with the launcher and window locator substituted.
///
/// The point of those being interfaces is exactly this: every branch of session
/// creation — packaged app, classic app, desktop, attach-to-window, and each
/// failure — is exercised here without a desktop, an installed application or a
/// UI session. The implementation being replaced launched processes inline in
/// the handler, so none of this could be tested and none of it was.
/// </summary>
[TestFixture]
public sealed class CreateSessionRouteTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
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

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private Task<HttpResponseMessage> CreateSession(object desiredCapabilities) =>
        _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities });

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Test]
    public async Task CreateSession_LaunchesTheApp_AndEchoesTheCapabilities()
    {
        const string App = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        HttpResponseMessage response = await CreateSession(new { app = App });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement produced = await BodyOf(response);
        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("session.create.calculator").ResponseBody!);

        produced.EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.RootElement.EnumerateObject().Select(p => p.Name));
        produced.GetProperty("status").GetInt32().ShouldBe(0);
        produced.GetProperty("value").GetProperty("app").GetString().ShouldBe(App);
        produced.GetProperty("sessionId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task CreateSession_PassesTheCapabilitiesThroughToTheLauncher()
    {
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(1, 2)));

        await CreateSession(new
        {
            app = @"C:\Windows\System32\notepad.exe",
            appArguments = "file.txt",
            appWorkingDir = @"C:\Temp",
        });

        // The measurement is what the launcher was asked for. Asserting only the
        // response would pass while the arguments were silently dropped, which is
        // exactly what CreateSessionWithArguments tests in the compatibility
        // suite check and what the previous implementation got wrong.
        // ApplicationTarget is a record, so this asserts the exact call rather
        // than a predicate over some of its fields — a target carrying an extra
        // wrong value cannot slip through, and there is no expression tree for
        // the compiler's null analysis to lose track of.
        _launcher.Received(1).Launch(new ApplicationTarget(
            App: @"C:\Windows\System32\notepad.exe",
            Arguments: "file.txt",
            WorkingDirectory: @"C:\Temp"));
    }

    [Test]
    public async Task CreateSession_StoresTheSession_SoLaterRequestsCanFindIt()
    {
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        HttpResponseMessage response = await CreateSession(new { app = "Calculator" });
        string sessionId = (await BodyOf(response)).GetProperty("sessionId").GetString()!;

        // The side effect, not the envelope: a session that is reported but not
        // stored fails on the very next request.
        ISessionStore store = _factory.Services.GetRequiredService<ISessionStore>();
        DriverSession? stored = store.Find(sessionId);

        stored.ShouldNotBeNull();
        stored.ProcessId.ShouldBe(4242);
        stored.WindowHandle.ShouldBe(0x1234);
    }

    [Test]
    public async Task CreateSession_GivesEachSessionADistinctId()
    {
        // The control on id generation. A handler returning a constant id would
        // satisfy every test above, and would then let one client's commands
        // reach another client's application.
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(1, 2)));

        string first = (await BodyOf(await CreateSession(new { app = "Calculator" })))
            .GetProperty("sessionId").GetString()!;
        string second = (await BodyOf(await CreateSession(new { app = "Calculator" })))
            .GetProperty("sessionId").GetString()!;

        first.ShouldNotBe(second);
    }

    [Test]
    public async Task CreateSession_WhenTheAppCannotBeStarted_ReportsUnknownError()
    {
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Failure("The system cannot find the file specified"));

        HttpResponseMessage response = await CreateSession(new { app = @"C:\this\does\not\exist.exe" });

        RecordedResponse recorded = RecordedResponses.Named("error.session.appNotFound");
        using JsonDocument recordedBody = JsonDocument.Parse(recorded.ResponseBody!);

        ((int)response.StatusCode).ShouldBe(recorded.HttpStatus);

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32()
            .ShouldBe(recordedBody.RootElement.GetProperty("status").GetInt32());
        produced.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("The system cannot find the file specified");
    }

    [Test]
    public async Task CreateSession_FailedLaunch_StoresNothing()
    {
        // A failed creation that left a session behind would leak one per attempt
        // and let a client drive a window that was never opened.
        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Failure("nope"));

        await CreateSession(new { app = "whatever" });

        _factory.Services.GetRequiredService<ISessionStore>().All().ShouldBeEmpty();
    }

    [Test]
    public async Task CreateSession_BadCapabilities_IsRejectedBeforeAnythingIsLaunched()
    {
        HttpResponseMessage response = await CreateSession(new { });

        RecordedResponse recorded = RecordedResponses.Named("error.session.badCapabilities");
        ((int)response.StatusCode).ShouldBe(recorded.HttpStatus);

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32().ShouldBe(100);
        produced.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Bad capabilities. Specify either app or appTopLevelWindow to create a session");

        // Validation must come first. Launching and then rejecting would leave an
        // application running that nothing will ever close.
        _launcher.DidNotReceive().Launch(Arg.Any<ApplicationTarget>());
    }

    [Test]
    public async Task CreateSession_RootApp_UsesTheDesktopWindow_WithoutLaunchingAnything()
    {
        _windows.DesktopWindow.Returns(0x1000C);

        HttpResponseMessage response = await CreateSession(new { app = "Root" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("value").GetProperty("app").GetString().ShouldBe("Root");

        string sessionId = produced.GetProperty("sessionId").GetString()!;
        DriverSession stored = _factory.Services.GetRequiredService<ISessionStore>().Find(sessionId)!;

        stored.WindowHandle.ShouldBe(0x1000C);

        // A desktop session belongs to no process, and nothing is started for it.
        stored.ProcessId.ShouldBe(0);
        _launcher.DidNotReceive().Launch(Arg.Any<ApplicationTarget>());
    }

    [Test]
    public async Task CreateSession_AttachesToAnExistingWindow_WithoutLaunchingAnything()
    {
        _windows.Exists(0xB822E2).Returns(true);
        // GetHostedProcessId, not GetOwningProcessId: a session tracks the process
        // whose CONTENT the window shows. For a packaged application those differ
        // — the frame belongs to ApplicationFrameHost — and terminating the wrong
        // one closes every UWP window on the machine.
        _windows.GetHostedProcessId(0xB822E2).Returns(9876);

        HttpResponseMessage response = await CreateSession(new { appTopLevelWindow = "B822E2" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string sessionId = (await BodyOf(response)).GetProperty("sessionId").GetString()!;
        DriverSession stored = _factory.Services.GetRequiredService<ISessionStore>().Find(sessionId)!;

        stored.WindowHandle.ShouldBe(0xB822E2);
        stored.ProcessId.ShouldBe(9876);
        _launcher.DidNotReceive().Launch(Arg.Any<ApplicationTarget>());
    }

    [Test]
    public async Task CreateSession_AttachToAMissingWindow_ReportsNoSuchWindow()
    {
        _windows.Exists(Arg.Any<nint>()).Returns(false);

        HttpResponseMessage response = await CreateSession(new { appTopLevelWindow = "DEADBEEF" });

        RecordedResponse recorded = RecordedResponses.Named("error.session.badTopLevelWindow");
        using JsonDocument recordedBody = JsonDocument.Parse(recorded.ResponseBody!);

        ((int)response.StatusCode).ShouldBe(recorded.HttpStatus);

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32()
            .ShouldBe(recordedBody.RootElement.GetProperty("status").GetInt32());
        produced.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Cannot find active window specified by capabilities: appTopLevelWindow");
    }

    [Test]
    public async Task CreateSession_AttachToWindow_ParsesTheHandleAsHexadecimal()
    {
        // WinAppDriver documents appTopLevelWindow as a hex string, so "B822E2"
        // is 12064994 and not 822 or a parse failure. Decimal parsing is the
        // plausible mistake and would look for an entirely different window.
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.GetHostedProcessId(Arg.Any<nint>()).Returns(1);

        await CreateSession(new { appTopLevelWindow = "B822E2" });

        _windows.Received().Exists(0xB822E2);
    }

    /// <summary>
    /// A handle attaches even when it carries the "0x" prefix this driver's own
    /// <c>/window_handle</c> emits.
    /// </summary>
    /// <remarks>
    /// <b>The untested seam.</b> Every other case here uses bare hex
    /// (<c>"B822E2"</c>), which <c>NumberStyles.HexNumber</c> accepts. It does
    /// NOT accept a leading "0x" — and <c>FormatHandle</c> in
    /// <c>WindowRoutes</c> always produces one. So a client that reads a real
    /// session's own handle back with <c>GET /window_handle</c> and feeds it
    /// straight into <c>appTopLevelWindow</c>, which is exactly what
    /// <c>CreateSessionFromExistingWindowHandle_ClassicApp</c> does, could never
    /// attach: this driver could not parse the handle it had just produced.
    /// </remarks>
    [Test]
    public async Task CreateSession_AttachToWindow_AcceptsTheZeroXPrefixItsOwnHandleFormatUses()
    {
        _windows.Exists(0xB822E2).Returns(true);
        _windows.GetHostedProcessId(0xB822E2).Returns(1);

        HttpResponseMessage response = await CreateSession(new { appTopLevelWindow = "0xB822E2" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _windows.Received().Exists(0xB822E2);
    }
}

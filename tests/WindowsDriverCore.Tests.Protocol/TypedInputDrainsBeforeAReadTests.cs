using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Typed input is waited for by the READ that depends on it, not by typing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured 2026-08-11.</b> <c>SendInput</c> only queues keystrokes; the
/// application consumes them on its own message loop. 52 characters were typed
/// and the client read back <c>ab</c> — the rest arrived during the next test,
/// whose assertion then failed on text it never typed.
/// </para>
/// <para>
/// <b>Why the wait is here and not in <c>/keys</c>.</b> It costs 46-195 ms.
/// Paying it per keystroke call would charge the suite three times for the
/// clear sequence it runs before every test, and would charge sessions that
/// never read. Typing stays at ~4 ms; the read pays once per burst. For scale,
/// WinAppDriver spends ~2500 ms typing the same string and still races.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TypedInputDrainsBeforeAReadTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.WaitForInputProcessed(Arg.Any<nint>()).Returns(true);
        _windows.BringToForeground(Arg.Any<nint>()).Returns(true);

        IKeyboardInput keyboard = Substitute.For<IKeyboardInput>();
        keyboard.Type(Arg.Any<string>()).Returns(true);

        IElementInspector inspector = Substitute.For<IElementInspector>();
        inspector.Text(Arg.Any<nint>(), Arg.Any<string>()).Returns(ElementRead.Success("whatever"));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(keyboard);
                services.AddSingleton(inspector);
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

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task Type(string sessionId) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/keys", UriKind.Relative),
            new StringContent("""{"value":["abc"]}""", Encoding.UTF8, "application/json"));

    private async Task ReadText(string sessionId) =>
        await _client.GetAsync(new Uri($"/session/{sessionId}/element/1.2/text", UriKind.Relative));

    [Test]
    public async Task ReadingAfterTyping_WaitsForTheKeystrokesToLand()
    {
        string sessionId = await NewSession();

        await Type(sessionId);
        await ReadText(sessionId);

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    [Test]
    public async Task Typing_DoesNotWait()
    {
        // The whole point of paying on the read. Waiting here would charge every
        // keystroke call ~100 ms, including the three-call clear the suite runs
        // before each test.
        string sessionId = await NewSession();

        await Type(sessionId);

        _windows.DidNotReceive().WaitForInputProcessed(Arg.Any<nint>());
    }

    [Test]
    public async Task ASecondReadDoesNotWaitAgain()
    {
        // Once drained, the input is landed. Charging every later read would make
        // the fix cost far more than it saves.
        string sessionId = await NewSession();

        await Type(sessionId);
        await ReadText(sessionId);
        await ReadText(sessionId);
        await ReadText(sessionId);

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    [Test]
    public async Task ASessionThatNeverTyped_NeverWaits()
    {
        // The control that keeps this from being a blanket cost. Finds, clicks
        // and reads in a session that has not typed must be untouched.
        string sessionId = await NewSession();

        await ReadText(sessionId);
        await ReadText(sessionId);

        _windows.DidNotReceive().WaitForInputProcessed(Arg.Any<nint>());
    }
}

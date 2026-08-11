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
/// Dispatched input is waited for by the READ that depends on it, not by the dispatch.
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
public sealed class DispatchedInputDrainsBeforeAReadTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IWindowLocator _windows = null!;

    /// <summary>
    /// Builds the server once. No other substitute here needs per-test rearming:
    /// nothing in this fixture reconfigures a default inline, and only
    /// <c>_windows</c> is ever asserted on with <c>Received</c>/
    /// <c>DidNotReceive</c>, which <see cref="ArrangeDefaults"/> resets.
    /// </summary>
    [OneTimeSetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.WaitForInputProcessed(Arg.Any<nint>()).Returns(true);
        _windows.BringToForeground(Arg.Any<nint>()).Returns(true);

        // THE MOUSE IS SUBSTITUTED, AND THAT IS NOT OPTIONAL.
        //
        // This fixture posts /click, and /click acts WHEREVER THE POINTER
        // ALREADY IS — there is no coordinate in the request to make it safe.
        // WebApplicationFactory boots the real container, so without this the
        // route resolves SendInputPointer and fires a genuine left click at
        // whatever the person running the suite happens to be pointing at.
        // Reported for real on 2026-08-11: "random clicks that click whatever my
        // mouse happens to be over".
        //
        // Same lesson as the injector in ActionsValidationTests, one route
        // later: a protocol test is about the wire, and any test that boots this
        // pipeline must substitute every path that can reach the desktop.
        IPointerInput pointer = Substitute.For<IPointerInput>();
        pointer.Click(Arg.Any<PointerButton>()).Returns(true);
        pointer.MoveTo(Arg.Any<int>(), Arg.Any<int>()).Returns(true);
        pointer.TryGetPosition(out Arg.Any<int>(), out Arg.Any<int>()).Returns(true);

        IKeyboardInput keyboard = Substitute.For<IKeyboardInput>();
        keyboard.Type(Arg.Any<string>()).Returns(true);

        // The carrying overload too, because /keys uses it: a session persists
        // modifiers between calls. Stubbing only the other one leaves this
        // returning false, the route reports the keystrokes were refused, and
        // the drain these tests measure never happens.
        keyboard.Type(Arg.Any<string>(), Arg.Any<HeldModifiers>()).Returns(true);

        // A click that actually happened. Without this the substitute returns a
        // default outcome, the route correctly declines to flag input that was
        // never dispatched, and the test would be measuring the stub.
        IElementInteractor interactor = Substitute.For<IElementInteractor>();
        interactor.Click(Arg.Any<nint>(), Arg.Any<string>())
            .Returns(ElementAction.Performed("test"));
        interactor.SendKeys(Arg.Any<nint>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ElementAction.Performed("keys"));

        IElementInspector inspector = Substitute.For<IElementInspector>();
        inspector.Text(Arg.Any<nint>(), Arg.Any<string>()).Returns(ElementRead.Success("whatever"));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(keyboard);
                services.AddSingleton(pointer);
                services.AddSingleton(inspector);
                services.AddSingleton(interactor);
            }));

        _client = _factory.CreateClient();
    }

    /// <summary>
    /// Rearms <c>_windows</c> before each test.
    /// </summary>
    /// <remarks>
    /// Every test here asserts <c>Received</c>/<c>DidNotReceive</c> on
    /// <c>WaitForInputProcessed</c>. Without clearing call history between
    /// tests, a later test's <c>DidNotReceive</c> would fail on a call that
    /// actually belonged to an earlier one.
    /// </remarks>
    [SetUp]
    public void ArrangeDefaults()
    {
        _windows.ClearReceivedCalls();
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.WaitForInputProcessed(Arg.Any<nint>()).Returns(true);
        _windows.BringToForeground(Arg.Any<nint>()).Returns(true);
    }

    [OneTimeTearDown]
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
    private async Task MouseClick(string sessionId) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/click", UriKind.Relative),
            new StringContent("{\"button\":0}", Encoding.UTF8, "application/json"));

    private async Task ClickElement(string sessionId) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/element/1.2/click", UriKind.Relative),
            new StringContent("{}", Encoding.UTF8, "application/json"));

    [Test]
    public async Task ReadingAfterAMouseClick_WaitsForItToLand()
    {
        // **The generalisation this fixture was missing.** InputPending was set
        // by the keyboard route ALONE, so a read after a click never waited. The
        // compatibility suite's MouseClick clicks num8Button and immediately
        // asserts the display reads "8"; it has never passed, under any window
        // root or desktop state, while its siblings now do.
        //
        // Typing and clicking are the same problem: SendInput queues, the
        // application consumes on its own message loop, and a read that outruns
        // that answers about a state the client never asked about.
        string sessionId = await NewSession();

        await MouseClick(sessionId);
        await ReadText(sessionId);

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    [Test]
    public async Task ReadingAfterAnElementClick_WaitsForItToLand()
    {
        // The element route dispatches through the same ladder and can end in a
        // real mouse click, so it owes the same wait.
        string sessionId = await NewSession();

        await ClickElement(sessionId);
        await ReadText(sessionId);

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    private async Task TypeIntoElement(string sessionId) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/element/1.2/value", UriKind.Relative),
            new StringContent("{\"value\":[\"abc\"]}", Encoding.UTF8, "application/json"));

    /// <summary>
    /// A read after <c>/element/{id}/value</c> waits for the typing to land.
    /// </summary>
    /// <remarks>
    /// <b>This route stopped needing the drain and then needed it again.</b> It
    /// used to write through ValuePattern, which is finished when the call
    /// returns, so it never flagged input as pending. Switching it to the
    /// keyboard made it asynchronous and nothing said so.
    ///
    /// The symptom was not a failure — it was a ROTATION. Measured 2026-08-11
    /// across two runs of identical code, four SendKeysToElement tests started
    /// passing and four different ones in the same family started failing, and
    /// the total never moved. A count that stays put is exactly how a race hides.
    /// </remarks>
    [Test]
    public async Task ReadingAfterTypingIntoAnElement_WaitsForItToLand()
    {
        string sessionId = await NewSession();

        await TypeIntoElement(sessionId);
        await ReadText(sessionId);

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    [Test]
    public async Task AMouseClick_DoesNotWaitAtDispatchTime()
    {
        // The same trade as typing: paid once by the read that depends on it,
        // not by every dispatch. A suite that clicks three times and reads once
        // pays 46-195 ms once rather than three times.
        string sessionId = await NewSession();

        await MouseClick(sessionId);

        _windows.DidNotReceive().WaitForInputProcessed(Arg.Any<nint>());
    }
}

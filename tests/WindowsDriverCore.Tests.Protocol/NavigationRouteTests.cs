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
        _keyboard.Type(Arg.Any<string>()).Returns(true);
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

        // THE EXACT KEY STRING, because the mechanism is the whole bug.
        // BuildBatch treats a carried HeldModifiers as already physically down
        // and does not press it, so the previous version sent a BARE Left arrow
        // - which moves the caret and leaves the view where it was - and the
        // test passed anyway because it asserted the same wrong mechanism the
        // route used. Alt must appear IN the string to be pressed.
        _keyboard.Received(1).Type($"{Alt}{LeftArrow}{Alt}");
    }

    /// <summary>
    /// The app finishes what it was doing before the next gesture is sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MEASURED on the guest by diffing a passing run against a failing one,
    /// same test and same commit family.</b> <c>NavigateBack_ModernApp</c> fails
    /// in 8 of 10 runs. In a PASSING run the second <c>/back</c> raises the
    /// discard dialog in 40 ms and the suite dismisses it; in a FAILING run no
    /// dialog appears and the application EXITS, so every later find answers
    /// <c>no such window</c>.
    /// </para>
    /// <para>
    /// The only difference upstream is speed. Time from the AddAlarm click to
    /// finding <c>EditAlarmHeader</c>:
    /// </para>
    /// <code>
    ///   passing run   122 ms
    ///   failing run    66 ms
    /// </code>
    /// <para>
    /// We reached the edit page twice as fast and sent Alt+Left before the page
    /// had set up its navigation state, so the gesture did not mean "go back
    /// within the app". WinAppDriver never hits this because it is slow enough
    /// to wait by accident — the same reason it survives the content-ready race
    /// and the typing race.
    /// </para>
    /// <para>
    /// <b>The primitive already exists and was only wired to reads.</b>
    /// <c>WaitForInputProcessed</c> was measured against three candidates and is
    /// the only one that worked (52 of 52 typed characters, where a synchronous
    /// <c>WM_NULL</c> and <c>GetQueueStatus</c> both reported ready while input
    /// was still queued). It sat in <c>ElementPropertyRoutes</c> alone, on the
    /// reasoning that a session which never types never pays. That is right for
    /// typing-then-reading and wrong here: this dependency is not read-after-write
    /// but <b>input-after-input</b>.
    /// </para>
    /// <para>
    /// It costs nothing when the application is idle, which is the ordinary
    /// case — the wait returns as soon as the condition holds rather than after
    /// a fixed interval.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Back_WaitsForEarlierInputToBeConsumed_BeforeSendingItsOwn()
    {
        string sessionId = await NewSession();

        // A click happened and has not been read back, which is exactly the
        // state the suite is in: it clicked AddAlarmButton, found the header,
        // and navigated - with no property read in between to drain it.
        _factory.Services.GetRequiredService<ISessionStore>()
            .Find(sessionId)!.InputPending = true;

        await Navigate(sessionId, "back");

        // ORDER IS THE ASSERTION. Both calls happening is not enough: a wait
        // performed after the keystroke waits for our own gesture and does
        // nothing for the one before it.
        Received.InOrder(() =>
        {
            _windows.WaitForInputProcessed(TheWindow);
            _keyboard.Type(Arg.Any<string>());
        });
    }

    /// <summary>
    /// With nothing outstanding, navigation does not wait at all.
    /// </summary>
    /// <remarks>
    /// <b>The control.</b> Waiting unconditionally would satisfy the test above
    /// and put a process-grained wait in front of every navigation in every
    /// session, including ones that have dispatched no input for it to drain.
    /// This driver's argument is speed; a wait that cannot be skipped is a cost
    /// paid for nothing.
    /// </remarks>
    [Test]
    public async Task Back_WithNothingOutstanding_DoesNotWait()
    {
        string sessionId = await NewSession();

        await Navigate(sessionId, "back");

        _windows.DidNotReceive().WaitForInputProcessed(Arg.Any<nint>());
        _keyboard.Received(1).Type(Arg.Any<string>());
    }

    [Test]
    public async Task Forward_SendsAltAndTheRightArrow()
    {
        // The control: without it, "always send Left" passes the test above and
        // makes forward navigate backwards.
        string sessionId = await NewSession();

        await Navigate(sessionId, "forward");

        _keyboard.Received(1).Type($"{Alt}{RightArrow}{Alt}");
    }

    [Test]
    public async Task TheAltIsBalanced_SoNothingIsLeftHeldAfterTheRequest()
    {
        // A modifier character toggles: the FIRST occurrence presses it and the
        // second releases it. An odd number leaves Alt down for the rest of the
        // process, which is measured - it took SendKeysToElement_ModifierAlt
        // down on the guest, reporting a modifier still active.
        //
        // Counting is the measurement here, not merely "Alt appears": a string
        // with one Alt would satisfy a Contains check and leave the key held.
        string sessionId = await NewSession();

        await Navigate(sessionId, "back");

        string sent = (string)_keyboard.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IKeyboardInput.Type))
            .GetArguments()[0]!;

        sent.Count(character => character == Alt).ShouldBe(2);
    }

    [Test]
    public async Task NoModifierIsLeftHeldAcrossRequests()
    {
        // ReleaseHeld is not called at all, and must not be: it sends a key-UP
        // for a modifier this route never left down, which is a lone Alt keyup
        // with no key-down before it.
        string sessionId = await NewSession();

        await Navigate(sessionId, "back");

        _keyboard.DidNotReceive().ReleaseHeld(Arg.Any<HeldModifiers>());
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

        _keyboard.DidNotReceive().Type(Arg.Any<string>());
    }

    /// <summary>The gesture is recorded as outstanding input.</summary>
    /// <remarks>
    /// <para>
    /// <b>Every other route that dispatches input records it, and this one did
    /// not.</b> <c>ActionRoutes</c>, <c>AlertRoutes</c>,
    /// <c>ElementActionRoutes</c>, <c>ExecuteRoutes</c>, <c>KeyboardRoutes</c>,
    /// <c>MouseRoutes</c> and <c>TouchRoutes</c> all set <c>InputPending</c>
    /// after dispatching. Navigation only ever CONSUMED the flag — it waited for
    /// somebody else's input before typing and left its own unaccounted for.
    /// </para>
    /// <para>
    /// The consequence is a read-after-write with nothing to wait on: a client
    /// that navigates and then reads is racing a gesture the driver knows it
    /// sent.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Back_RecordsItsOwnGesture_SoALaterReadCanWaitForIt()
    {
        string sessionId = await NewSession();

        await Navigate(sessionId, "back");

        _factory.Services.GetRequiredService<ISessionStore>()
            .Find(sessionId)!.InputPending
            .ShouldBeTrue("the gesture was dispatched and nothing has consumed it");
    }

    /// <summary>A gesture the system refused is not recorded.</summary>
    /// <remarks>
    /// <b>The control.</b> Setting the flag unconditionally would pass the test
    /// above and make every later read wait for input that was never sent —
    /// paying the drain's cost on a path where there is nothing to drain, which
    /// is the one thing <c>Back_WithNothingOutstanding_DoesNotWait</c> exists to
    /// prevent.
    /// </remarks>
    [Test]
    public async Task AGestureTheSystemRefused_IsNotRecorded()
    {
        string sessionId = await NewSession();

        _keyboard.Type(Arg.Any<string>()).Returns(false);

        await Navigate(sessionId, "back");

        _factory.Services.GetRequiredService<ISessionStore>()
            .Find(sessionId)!.InputPending
            .ShouldBeFalse("nothing was dispatched, so nothing is outstanding");
    }
}

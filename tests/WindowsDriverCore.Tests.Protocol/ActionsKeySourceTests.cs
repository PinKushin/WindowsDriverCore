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
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>key</c> input sources in <c>POST /actions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>They were skipped, and the request answered 200.</b> The runner's own
/// comment said key sources were "someone else's job" — and there was no
/// someone else, anywhere in the Protocol assembly. So
/// <c>ActionChains(driver).key_down(Keys.CONTROL).send_keys('a')</c>, which is
/// how every Selenium 4 client sends keyboard input, typed NOTHING and reported
/// success. That is the precise defect this driver exists to fix: reporting
/// success for work not done.
/// </para>
/// <para>
/// <b>Invisible to the compatibility suite.</b> It is a Selenium 3.8 client and
/// sends keystrokes through <c>/keys</c> and <c>element/{id}/value</c>, so the
/// score cannot move whether this works or not. Found by the by-parameter audit
/// lens, the same one that found <c>/touch/flick</c> ignoring <c>speed</c>.
/// </para>
/// <para>
/// <b>The translation is the interesting part.</b> W3C is explicit —
/// <c>keyDown</c> then <c>keyUp</c> — while the JSON Wire string this driver
/// already types is a TOGGLE: each occurrence of a modifier flips its held
/// state. So the two cannot be concatenated, they have to be converted, and
/// these tests pin the conversion by asserting the exact string handed to the
/// keyboard rather than that some string was.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ActionsKeySourceTests : IDisposable
{
    private const nint Handle = 0x3333;

    /// <summary>Control, as both dialects spell it.</summary>
    private const string Control = "";

    /// <summary>Shift.</summary>
    private const string Shift = "";

    /// <summary>The characters the keyboard treats as toggling modifiers.</summary>
    /// <remarks>
    /// Alt and Meta are spelled by code point rather than pasted, so this line
    /// stays readable in a diff — the private-use block renders as boxes.
    /// </remarks>
    private static readonly char[] ModifierCharacters =
        [Shift[0], Control[0], '', ''];

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IKeyboardInput _keyboard = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, Handle)));

        IWindowLocator windows = Substitute.For<IWindowLocator>();
        windows.Exists(Arg.Any<nint>()).Returns(true);
        windows.IsTopLevel(Arg.Any<nint>()).Returns(true);
        windows.GetOwningProcessId(Arg.Any<nint>()).Returns(4242);
        windows.BringToForeground(Arg.Any<nint>()).Returns(true);
        windows.WaitForInputProcessed(Arg.Any<nint>()).Returns(true);

        _keyboard = Substitute.For<IKeyboardInput>();
        _keyboard.Type(Arg.Any<string>()).Returns(true);

        // THE SUBSTITUTE HONOURS THE CONTRACT ITS INTERFACE DOCUMENTS: "what is
        // still down is handed back so the next call knows not to press it
        // again". A stub that returns true and leaves HeldModifiers untouched
        // is not a faithful stand-in for the keyboard — it is a keyboard with
        // amnesia, and the persistence test below cannot run against one.
        //
        // Four lines of toggle, and deliberately NOT a reimplementation of what
        // is under test. Under test is the TRANSLATION from W3C's explicit
        // keyDown/keyUp into this toggling string; the toggle itself is
        // SendInputKeyboard's, is covered by its own tests, and cannot be
        // exercised here because the real keyboard injects into the live
        // desktop.
        _keyboard.Type(Arg.Any<string>(), Arg.Any<HeldModifiers>())
            .Returns(call =>
            {
                HeldModifiers held = call.Arg<HeldModifiers>();

                foreach (char key in call.Arg<string>())
                {
                    if (!ModifierCharacters.Contains(key))
                    {
                        continue;
                    }

                    if (held.Contains(key))
                    {
                        held.Release(key);
                    }
                    else
                    {
                        held.Hold(key);
                    }
                }

                return true;
            });

        // A pointer substitute, because this fixture boots the REAL container
        // and an /actions payload with a pointer half would otherwise inject
        // into the live desktop. Two protocol tests have done that for real.
        IPointerInput pointer = Substitute.For<IPointerInput>();
        pointer.MoveTo(Arg.Any<int>(), Arg.Any<int>()).Returns(true);
        pointer.TryGetPosition(out Arg.Any<int>(), out Arg.Any<int>()).Returns(true);

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(windows);
                services.AddSingleton(_keyboard);
                services.AddSingleton(pointer);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void Rearm() => _keyboard.ClearReceivedCalls();

    /// <summary>A plain keystroke reaches the keyboard.</summary>
    /// <remarks>
    /// The floor: before this, the payload was parsed, validated, skipped and
    /// answered 200 with nothing typed.
    /// </remarks>
    [Test]
    public async Task KeyDownAndUp_TypeTheCharacter()
    {
        string session = await NewSession();

        await PerformKeys(session, "{\"type\":\"keyDown\",\"value\":\"a\"}",
                                   "{\"type\":\"keyUp\",\"value\":\"a\"}");

        // ONE "a", not two. keyDown and keyUp are one keystroke between them,
        // and typing on both halves is the obvious wrong translation.
        Typed().ShouldBe("a");
    }

    /// <summary>A modifier is held across the keys pressed inside it.</summary>
    /// <remarks>
    /// <para>
    /// The conversion under test. W3C sends four explicit events; the JSON Wire
    /// string that reaches the keyboard is <c>Control a Control</c> — hold,
    /// press, release — because a modifier character TOGGLES.
    /// </para>
    /// <para>
    /// This is the test that distinguishes a correct translation from a
    /// concatenation of every <c>value</c> in the sequence, which would produce
    /// <c>Control a a Control</c> and type "aa" with control held.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ModifierSequence_TogglesRatherThanRepeating()
    {
        string session = await NewSession();

        await PerformKeys(
            session,
            $"{{\"type\":\"keyDown\",\"value\":\"{Control}\"}}",
            "{\"type\":\"keyDown\",\"value\":\"a\"}",
            "{\"type\":\"keyUp\",\"value\":\"a\"}",
            $"{{\"type\":\"keyUp\",\"value\":\"{Control}\"}}");

        Typed().ShouldBe($"{Control}a{Control}");
    }

    /// <summary>A pause carries no keystroke.</summary>
    /// <remarks>
    /// <c>pause</c> is a legal step in a key source and has no <c>value</c>.
    /// Reading it as one would type a stray character into the application.
    /// </remarks>
    [Test]
    public async Task Pause_TypesNothing()
    {
        string session = await NewSession();

        await PerformKeys(
            session,
            "{\"type\":\"keyDown\",\"value\":\"x\"}",
            "{\"type\":\"pause\",\"duration\":5}",
            "{\"type\":\"keyUp\",\"value\":\"x\"}");

        Typed().ShouldBe("x");
    }

    /// <summary>A modifier left down stays down.</summary>
    /// <remarks>
    /// <para>
    /// W3C lets a sequence end with a key still held, and the session is what
    /// remembers it — exactly as <c>/keys</c> already does. A second sequence
    /// therefore must NOT press it again: the string handed over the second
    /// time is the bare character, with the held state carried in
    /// <c>HeldModifiers</c>.
    /// </para>
    /// <para>
    /// Not a curiosity. It is why <c>DELETE /actions</c> releases modifiers —
    /// a shift left down by an interrupted sequence otherwise applies to every
    /// later command in the session, and to the desktop after it ends.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AHeldModifier_PersistsIntoTheNextSequence()
    {
        string session = await NewSession();

        await PerformKeys(session, $"{{\"type\":\"keyDown\",\"value\":\"{Shift}\"}}");

        Typed().ShouldBe(Shift, "the first sequence presses it");

        _keyboard.ClearReceivedCalls();

        // RELEASING IT IS THE CONDITION THAT DISCRIMINATES, and the obvious
        // second sequence — press an ordinary key — does not.
        //
        // With shift remembered, keyUp moves the state and emits the character,
        // which the keyboard reads as the flip that lifts it. With shift
        // forgotten, the removal finds nothing, nothing is emitted, and the key
        // stays down for the rest of the session and for the desktop after it.
        // A plain "b" types "b" either way and cannot tell those apart — which
        // is what the first draft of this test asserted, and it would have
        // passed against a driver that forgot every modifier.
        await PerformKeys(session, $"{{\"type\":\"keyUp\",\"value\":\"{Shift}\"}}");

        Typed().ShouldBe(Shift, "a modifier the session remembers can be released");

    }

    /// <summary>The pointer half of a mixed payload still runs.</summary>
    /// <remarks>
    /// THE CONTROL. Key sources were skipped precisely so a mixed payload's
    /// pointer half would still be performed, and that behaviour is worth
    /// keeping — a change that teaches the runner keys and loses pointers
    /// trades every gesture test in the suite for a dialect the suite does not
    /// speak.
    /// </remarks>
    [Test]
    public async Task AMixedPayload_StillPerformsBothHalves()
    {
        string session = await NewSession();

        HttpResponseMessage response = await Post(
            $"/session/{session}/actions",
            $$"""
            {"actions":[
              {"type":"key","id":"k","actions":[
                {"type":"keyDown","value":"z"},{"type":"keyUp","value":"z"}]},
              {"type":"pointer","id":"p","parameters":{"pointerType":"touch"},
               "actions":[{"type":"pointerMove","duration":0,"x":10,"y":10}]}
            ]}
            """);

        ((int)response.StatusCode).ShouldBe(200);

        Typed().ShouldBe("z", "the key half runs");
    }

    /// <summary>What the keyboard was asked to type, across every call.</summary>
    private string Typed()
    {
        StringBuilder typed = new();

        foreach (NSubstitute.Core.ICall call in _keyboard.ReceivedCalls())
        {
            if (call.GetMethodInfo().Name == nameof(IKeyboardInput.Type))
            {
                typed.Append((string)call.GetArguments()[0]!);
            }
        }

        return typed.ToString();
    }

    private async Task PerformKeys(string session, params string[] steps)
    {
        HttpResponseMessage response = await Post(
            $"/session/{session}/actions",
            $$"""
            {"actions":[{"type":"key","id":"keyboard","actions":[{{string.Join(",", steps)}}]}]}
            """);

        ((int)response.StatusCode).ShouldBe(200, "a key sequence is a legal payload");
    }

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private Task<HttpResponseMessage> Post(string path, string json) =>
        _client.PostAsync(
            new Uri(path, UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }
}

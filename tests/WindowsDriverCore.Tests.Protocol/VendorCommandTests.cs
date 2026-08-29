using System;
using System.Collections.Generic;
using System.Text.Json;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Routing;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The <c>windows:</c> vendor commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>WinAppDriver serves none of these, which was measured rather than
/// assumed.</b> Probed 2026-08-29 against 1.2.2009: <c>POST /execute</c> answers
/// 501 for every script, including <c>windows: click</c>, while an invented
/// route answers 404 — so the reference routes the command and implements
/// nothing. The vocabulary is appium-windows-driver's, and serving it is going
/// beyond the reference rather than matching it.
/// </para>
/// <para>
/// <b>Nothing in the compatibility suite touches any of this</b>, so every
/// assertion here is the only thing standing between a command and silently
/// doing nothing.
/// </para>
/// </remarks>
[TestFixture]
public sealed class VendorCommandTests
{
    private const nint Window = 0x7001;
    private const int ProcessId = 4242;

    private IPointerInput _mouse = null!;
    private IKeyboardInput _keyboard = null!;
    private IClipboard _clipboard = null!;
    private IElementInspector _inspector = null!;
    private IWindowLocator _windows = null!;
    private VendorCommandRunner _runner = null!;
    private DriverSession _session = null!;

    [SetUp]
    public void Arrange()
    {
        _mouse = Substitute.For<IPointerInput>();
        _mouse.MoveTo(Arg.Any<int>(), Arg.Any<int>()).Returns(true);
        _mouse.Click(Arg.Any<PointerButton>()).Returns(true);
        _mouse.DoubleClick(Arg.Any<PointerButton>()).Returns(true);
        _mouse.Press(Arg.Any<PointerButton>()).Returns(true);
        _mouse.Release(Arg.Any<PointerButton>()).Returns(true);
        _mouse.Scroll(Arg.Any<int>(), Arg.Any<int>()).Returns(true);
        _mouse.TryGetPosition(out Arg.Any<int>(), out Arg.Any<int>()).Returns(true);

        _keyboard = Substitute.For<IKeyboardInput>();
        _keyboard.Type(Arg.Any<string>(), Arg.Any<HeldModifiers>()).Returns(true);

        _clipboard = Substitute.For<IClipboard>();
        _clipboard.TryWrite(Arg.Any<string>()).Returns(true);

        _inspector = Substitute.For<IElementInspector>();

        _windows = Substitute.For<IWindowLocator>();
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(100, 200, 800, 600));
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);

        _runner = new VendorCommandRunner(_windows, _inspector, _mouse, _keyboard, _clipboard);

        _session = new DriverSession(
            "vendor-session",
            new Dictionary<string, string>(StringComparer.Ordinal),
            ProcessId,
            Window,
            OwnsApplication: true);
    }

    /// <summary>Coordinates are window-relative, not screen-relative.</summary>
    /// <remarks>
    /// <b>The one that has caused real damage before.</b> A viewport origin is
    /// measured from the window; treating x and y as screen coordinates sends
    /// genuine mouse input into whatever application happens to be at that screen
    /// point. The pointer path shipped without this guard once and clicked into
    /// another program.
    /// </remarks>
    [Test]
    public void ClickCoordinates_AreRelativeToTheWindow()
    {
        Run("windows: click", """{"x": 10, "y": 20}""").Refusal.ShouldBeNull();

        // The window is at (100, 200), so (10, 20) inside it is (110, 220).
        _mouse.Received(1).MoveTo(110, 220);
    }

    /// <summary>A point outside the session's window is refused.</summary>
    /// <remarks>
    /// THE CONTROL for the guard above, and the assertion that matters: not only
    /// that a refusal came back, but that NO CLICK WAS SENT. A version that
    /// refused after clicking would pass a status-only assertion and still have
    /// put input on someone else's window.
    /// </remarks>
    [Test]
    public void APointOutsideTheWindow_IsRefusedWithoutClicking()
    {
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(false);

        Run("windows: click", """{"x": 10, "y": 20}""").Refusal
            .ShouldNotBeNull()
            .ShouldContain("outside the session window");

        _mouse.DidNotReceive().Click(Arg.Any<PointerButton>());
    }

    /// <summary>An element is clicked at its centre.</summary>
    [Test]
    public void AnElementId_ResolvesToTheElementsCentre()
    {
        _inspector.ScreenBounds(Window, "e7")
            .Returns(ElementRead.Success(new ElementBounds(300, 400, 60, 20)));

        Run("windows: click", """{"elementId": "e7"}""").Refusal.ShouldBeNull();

        // A CORNER IS INSIDE THE RECTANGLE AND OUTSIDE MOST CONTROLS' HIT AREA,
        // so the centre is the only defensible point.
        _mouse.Received(1).MoveTo(330, 410);
    }

    /// <summary>The button is honoured.</summary>
    [Test]
    public void AStatedButton_IsTheOneClicked()
    {
        Run("windows: click", """{"x": 1, "y": 1, "button": "right"}""").Refusal.ShouldBeNull();

        _mouse.Received(1).Click(PointerButton.Right);
        _mouse.DidNotReceive().Click(PointerButton.Left);
    }

    /// <summary>Modifiers are held across the click and lifted after.</summary>
    /// <remarks>
    /// The whole reason a caller states them here rather than bracketing the
    /// click with <c>/keys</c> calls. Lifting them is not optional: a modifier
    /// left down applies to everything the session does next, and to the desktop
    /// once it ends.
    /// </remarks>
    [Test]
    public void ModifierKeys_AreHeldAndThenLifted()
    {
        Run("windows: click", """{"x": 1, "y": 1, "modifierKeys": ""}""")
            .Refusal.ShouldBeNull();

        _keyboard.Received(1).Type("", Arg.Any<HeldModifiers>());
        _keyboard.Received(1).ReleaseHeld(Arg.Any<HeldModifiers>());
    }

    /// <summary>doubleClick goes through the double-click path.</summary>
    /// <remarks>
    /// Not two ordinary clicks in a loop. Windows decides what counts as a double
    /// click from its own double-click TIME, so two separate clicks a few
    /// milliseconds apart are a different event from the one the caller asked
    /// for.
    /// </remarks>
    [Test]
    public void DoubleClick_UsesTheDoubleClickPath()
    {
        Run("windows: doubleClick", """{"x": 1, "y": 1}""").Refusal.ShouldBeNull();

        _mouse.Received(1).DoubleClick(PointerButton.Left);
        _mouse.DidNotReceive().Click(Arg.Any<PointerButton>());
    }

    /// <summary>The wheel turns where the pointer is.</summary>
    /// <remarks>
    /// A wheel event goes to the window UNDER THE CURSOR, not the focused one, so
    /// the move is part of the command rather than something the caller has to
    /// remember to send first.
    /// </remarks>
    [Test]
    public void Scroll_MovesThePointerThenTurnsTheWheel()
    {
        Run("windows: scroll", """{"x": 5, "y": 5, "deltaY": -3}""").Refusal.ShouldBeNull();

        _mouse.Received(1).MoveTo(105, 205);
        _mouse.Received(1).Scroll(0, -3);
    }

    /// <summary>A scroll of nothing is refused rather than dispatched.</summary>
    /// <remarks>
    /// <b>The control for scroll.</b> A caller that names no delta has asked for
    /// nothing, and answering 200 would report a scroll that never happened —
    /// the defect this driver exists to fix. Refusing is also what distinguishes
    /// "I read your deltas" from "I ignored them", which is exactly how
    /// <c>/touch/flick</c>'s <c>speed</c> hid for the life of the route.
    /// </remarks>
    [Test]
    public void AScrollWithNoDelta_IsRefused()
    {
        Run("windows: scroll", """{"x": 5, "y": 5}""").Refusal
            .ShouldNotBeNull()
            .ShouldContain("deltaX");

        _mouse.DidNotReceive().Scroll(Arg.Any<int>(), Arg.Any<int>());
    }

    /// <summary>A drag presses, moves, and releases in that order.</summary>
    [Test]
    public void ClickAndDrag_PressesMovesAndReleases()
    {
        Run("windows: clickAndDrag", """{"startX": 1, "startY": 2, "endX": 30, "endY": 40}""")
            .Refusal.ShouldBeNull();

        Received.InOrder(() =>
        {
            _mouse.MoveTo(101, 202);
            _mouse.Press(PointerButton.Left);
            _mouse.MoveTo(130, 240);
            _mouse.Release(PointerButton.Left);
        });
    }

    /// <summary>A failed move still releases the button.</summary>
    /// <remarks>
    /// <b>The control that guards the desktop.</b> Returning early on a failed
    /// move would leave the left mouse button PHYSICALLY DOWN — every subsequent
    /// pointer movement becomes a drag, in every application, until something
    /// else releases it.
    /// </remarks>
    [Test]
    public void ADragWhoseMoveFails_StillReleasesTheButton()
    {
        _mouse.MoveTo(130, 240).Returns(false);

        Run("windows: clickAndDrag", """{"startX": 1, "startY": 2, "endX": 30, "endY": 40}""")
            .Refusal.ShouldNotBeNull();

        _mouse.Received(1).Release(PointerButton.Left);
    }

    /// <summary>A virtual key code is typed as its WebDriver character.</summary>
    /// <remarks>
    /// appium-windows-driver states keystrokes as <c>virtualKeyCode</c>; this
    /// driver types WebDriver's private-use characters. 0x0D is VK_RETURN.
    /// </remarks>
    [Test]
    public void AVirtualKeyCode_IsTypedAsItsWebDriverCharacter()
    {
        Run("windows: keys", """{"actions": [{"virtualKeyCode": 13}]}""")
            .Refusal.ShouldBeNull();

        _keyboard.Received(1).Type("", Arg.Any<HeldModifiers>());
    }

    /// <summary>A virtual key with no WebDriver spelling is refused, not guessed.</summary>
    /// <remarks>
    /// <b>The control for the key mapping.</b> Most virtual keys — the function
    /// keys, the numeric keypad, media keys — have no character in the map. A
    /// lookup that fell back to something plausible would type the WRONG KEY and
    /// report success, which is worse than refusing. 0x70 is VK_F1.
    /// </remarks>
    [Test]
    public void AVirtualKeyWithNoSpelling_IsRefusedRatherThanGuessed()
    {
        Run("windows: keys", """{"actions": [{"virtualKeyCode": 112}]}""").Refusal
            .ShouldNotBeNull()
            .ShouldContain("112");

        _keyboard.DidNotReceive().Type(Arg.Any<string>(), Arg.Any<HeldModifiers>());
    }

    /// <summary>Text is typed as it stands.</summary>
    [Test]
    public void Text_IsTypedVerbatim()
    {
        Run("windows: keys", """{"actions": [{"text": "hi"}, {"virtualKeyCode": 13}]}""")
            .Refusal.ShouldBeNull();

        _keyboard.Received(1).Type("hi", Arg.Any<HeldModifiers>());
    }

    /// <summary>A pause is refused rather than silently dropped.</summary>
    /// <remarks>
    /// This driver has no timed wait in an input path by rule. Delivering the
    /// keys untimed and answering 200 would report a timed sequence was
    /// delivered when it was not.
    /// </remarks>
    [Test]
    public void APause_IsRefusedRatherThanIgnored()
    {
        Run("windows: keys", """{"actions": [{"text": "a"}, {"pause": 500}]}""").Refusal
            .ShouldNotBeNull()
            .ShouldContain("pause");

        _keyboard.DidNotReceive().Type(Arg.Any<string>(), Arg.Any<HeldModifiers>());
    }

    /// <summary>Clipboard content round-trips, base64 included.</summary>
    [Test]
    public void SetClipboard_AcceptsPlainAndBase64Content()
    {
        Run("windows: setClipboard", """{"content": "plain"}""").Refusal.ShouldBeNull();
        _clipboard.Received(1).TryWrite("plain");

        // "aGVsbG8=" is "hello".
        Run("windows: setClipboard", """{"b64Content": "aGVsbG8="}""").Refusal.ShouldBeNull();
        _clipboard.Received(1).TryWrite("hello");
    }

    /// <summary>Malformed base64 is refused, not pasted literally.</summary>
    /// <remarks>
    /// THE CONTROL for the decode. Falling back to the raw string would paste
    /// "not!base64" when the caller asked for whatever it decoded to — a silent
    /// corruption of their data rather than an error they can see.
    /// </remarks>
    [Test]
    public void MalformedBase64_IsRefusedRatherThanPastedLiterally()
    {
        Run("windows: setClipboard", """{"b64Content": "not!base64"}""").Refusal.ShouldNotBeNull();

        _clipboard.DidNotReceive().TryWrite(Arg.Any<string>());
    }

    /// <summary>A clipboard with no text is a failed read, not an empty one.</summary>
    /// <remarks>
    /// A clipboard holding an image answers false. Reporting "" would tell the
    /// caller it was empty when it was not.
    /// </remarks>
    [Test]
    public void AClipboardWithNoText_IsRefusedRatherThanReportedEmpty()
    {
        _clipboard.TryRead(out Arg.Any<string?>()).Returns(false);

        Run("windows: getClipboard", null).Refusal.ShouldNotBeNull();
    }

    /// <summary>Raw JavaScript is refused with a reason.</summary>
    /// <remarks>
    /// WinAppDriver answers a bare 501 with no body. A UIA tree is not a document
    /// and there is nothing for JavaScript to run against, and saying so — plus
    /// naming what IS supported — is the difference between a client that can act
    /// on the answer and one that guesses.
    /// </remarks>
    [Test]
    public void RawJavaScript_IsRefusedWithAReasonAndTheVocabulary()
    {
        string? refusal = Run("return document.title;", null).Refusal;

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("no script engine");
        refusal.ShouldContain("windows: click", Case.Sensitive);
    }

    /// <summary>An unknown vendor command names the ones that exist.</summary>
    /// <remarks>
    /// The vocabulary is not guessable, so "unknown command" alone is the least
    /// actionable error a client can receive.
    /// </remarks>
    [Test]
    public void AnUnknownVendorCommand_NamesWhatIsSupported()
    {
        string? refusal = Run("windows: teleport", null).Refusal;

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("windows: teleport");
        refusal.ShouldContain("windows: setClipboard");
    }

    /// <summary>Arbitrary shell execution is not served.</summary>
    /// <remarks>
    /// <b>Deliberate, and asserted so it stays that way.</b>
    /// appium-windows-driver has <c>windows: execPowerShell</c>, which runs a
    /// shell command on the machine hosting the driver — remote code execution
    /// reachable by anything that can open a socket to this process. A future
    /// change that adds it "for completeness" fails here first.
    /// </remarks>
    [Test]
    public void ExecPowerShell_IsNotServed()
    {
        Run("windows: execPowerShell", """{"command": "echo hi"}""").Refusal.ShouldNotBeNull();

        VendorCommandRunner.Supported.ShouldNotContain("windows: execPowerShell");
    }

    /// <summary>Modifiers are honoured by every command, not just click.</summary>
    /// <remarks>
    /// <b>Found by the audit's by-parameter lens, inside this driver's own new
    /// code.</b> <c>modifierKeys</c> was read by <c>click</c> and silently
    /// dropped by the other three — and Ctrl+scroll is zoom in most
    /// applications, so a zoom request was becoming a scroll and reporting
    /// success. Exactly the shape of the <c>speed</c> defect that started the
    /// audit, reintroduced while closing it.
    /// </remarks>
    [TestCase("windows: scroll", """{"x":1,"y":1,"deltaY":-1,"modifierKeys":""}""")]
    [TestCase("windows: hover", """{"x":1,"y":1,"modifierKeys":""}""")]
    [TestCase("windows: clickAndDrag",
        """{"startX":1,"startY":1,"endX":9,"endY":9,"modifierKeys":""}""")]
    public void EveryPointerCommand_HoldsTheStatedModifiers(string script, string argument)
    {
        Run(script, argument).Refusal.ShouldBeNull();

        _keyboard.Received(1).Type("", Arg.Any<HeldModifiers>());
        _keyboard.Received(1).ReleaseHeld(Arg.Any<HeldModifiers>());
    }

    /// <summary>A parameter this driver does not implement is refused by name.</summary>
    /// <remarks>
    /// <para>
    /// <b>The protocol audit generalised into a guard.</b> Every gap the audit
    /// found had one shape — a key read into a local and dropped, or never read,
    /// with the request answered 200 — and each was found by hand, one at a time,
    /// months apart: <c>speed</c>, <c>width</c>, <c>height</c>, <c>twist</c>,
    /// <c>modifierKeys</c>.
    /// </para>
    /// <para>
    /// Refusing at the boundary converts that whole class from a silent wrong
    /// answer into a loud one, and it needs no inventory of what a caller MIGHT
    /// send — only what this driver implements, which is knowable exactly.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>Every command, not one.</b> The first version of this test sent the
    /// stray key to <c>click</c> alone — and a mutation that widened
    /// <c>scroll</c>'s accepted list survived it, because the guard was only ever
    /// exercised on one entry of a per-command table. A table with eight rows
    /// needs the test to walk all eight.
    /// </remarks>
    [TestCase("windows: click", """{"x":1,"y":1""")]
    [TestCase("windows: doubleClick", """{"x":1,"y":1""")]
    [TestCase("windows: hover", """{"x":1,"y":1""")]
    [TestCase("windows: scroll", """{"x":1,"y":1,"deltaY":-1""")]
    [TestCase("windows: clickAndDrag", """{"startX":1,"startY":1,"endX":9,"endY":9""")]
    [TestCase("windows: keys", """{"actions":[]""")]
    [TestCase("windows: setClipboard", "{\"content\":\"ok\"")]
    [TestCase("windows: getClipboard", """{""")]
    public void AParameterThisDriverDoesNotImplement_IsRefusedByName(string script, string valid)
    {
        string separator = valid.EndsWith('{') ? string.Empty : ",";

        string? refusal = Run(script, $$"""{{valid}}{{separator}}"durationMs":500}""").Refusal;

        refusal.ShouldNotBeNull($"{script} must refuse a parameter it does not implement");
        refusal.ShouldContain("durationMs");

        // AND NOTHING WAS DISPATCHED. A guard that refused after acting would
        // pass a message assertion and still have moved the mouse.
        _mouse.DidNotReceive().Click(Arg.Any<PointerButton>());
        _mouse.DidNotReceive().Scroll(Arg.Any<int>(), Arg.Any<int>());
        _keyboard.DidNotReceive().Type(Arg.Any<string>(), Arg.Any<HeldModifiers>());
        _clipboard.DidNotReceive().TryWrite(Arg.Any<string>());
    }

    /// <summary>A parameter belonging to a DIFFERENT command is still refused.</summary>
    /// <remarks>
    /// <b>The control that stops the guard being pooled.</b> A single shared list
    /// of every key any command reads would accept <c>deltaY</c> on a click —
    /// which is the same "we read it somewhere, so it is fine" reasoning that let
    /// <c>modifierKeys</c> be honoured by one command and dropped by three.
    /// </remarks>
    [Test]
    public void AParameterFromAnotherCommand_IsRefusedToo()
    {
        Run("windows: click", """{"x":1,"y":1,"deltaY":3}""").Refusal
            .ShouldNotBeNull()
            .ShouldContain("deltaY");
    }

    /// <summary>The parameters each command DOES implement are still accepted.</summary>
    /// <remarks>
    /// <b>The control for the guard itself.</b> A refusal that fired on
    /// everything would pass both tests above and break every command — the
    /// cheapest possible way to make this file look green while the driver
    /// answers nothing.
    /// </remarks>
    [Test]
    public void TheParametersEachCommandImplements_AreAccepted()
    {
        Run("windows: click",
            """{"elementId":"","x":1,"y":1,"button":"left","modifierKeys":"","times":1}""")
            .Refusal.ShouldBeNull();

        Run("windows: setClipboard", """{"content":"ok"}""").Refusal.ShouldBeNull();
        Run("windows: getClipboard", null).Refusal.ShouldNotBeNull("no clipboard content stubbed");
    }

    private VendorOutcome Run(string script, string? argument)
    {
        if (argument is null)
        {
            return _runner.Run(script, null, _session);
        }

        using JsonDocument document = JsonDocument.Parse(argument);
        return _runner.Run(script, document.RootElement.Clone(), _session);
    }
}

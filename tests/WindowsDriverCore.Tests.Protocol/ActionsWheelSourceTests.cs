using System.Text.Json;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Routing;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>wheel</c> input sources in <c>POST /actions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third of the three source types W3C defines, and the second one this
/// route was silently dropping.</b> <c>/actions</c> takes <c>pointer</c>,
/// <c>key</c> and <c>wheel</c>; only the first was performed, and all three were
/// answered 200.
/// </para>
/// <para>
/// <b>Found by accident rather than by the audit</b>, while adding a mouse wheel
/// for <c>windows: scroll</c> — there was no wheel capability in the codebase at
/// all, which is what made the missing source visible. Worth recording: the audit
/// found the key sources and walked past this one twice.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ActionsWheelSourceTests
{
    private const nint Window = 0x8001;

    private IPointerInput _mouse = null!;
    private IWindowLocator _windows = null!;
    private WheelActionRunner _runner = null!;

    [SetUp]
    public void Arrange()
    {
        _mouse = Substitute.For<IPointerInput>();
        _mouse.MoveTo(Arg.Any<int>(), Arg.Any<int>()).Returns(true);
        _mouse.Scroll(Arg.Any<int>(), Arg.Any<int>()).Returns(true);

        _windows = Substitute.For<IWindowLocator>();
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(100, 200, 800, 600));
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);

        _runner = new WheelActionRunner(_mouse, _windows);
    }

    /// <summary>A scroll reaches the wheel.</summary>
    /// <remarks>
    /// The floor: before this the payload validated, was skipped, and answered
    /// 200 with nothing turned.
    /// </remarks>
    [Test]
    public void AScrollSource_TurnsTheWheel()
    {
        Perform("""{"type":"scroll","x":10,"y":20,"deltaX":0,"deltaY":100}""")
            .ShouldBeNull();

        _mouse.Received(1).Scroll(0, -1);
    }

    /// <summary>The sign is inverted between W3C and Win32.</summary>
    /// <remarks>
    /// <b>The subtlety of the whole source.</b> W3C states <c>deltaY</c> like a
    /// scrollbar position — positive scrolls content DOWN. Windows states it like
    /// a physical wheel — positive is rotated away from the user, which scrolls
    /// content UP. Passing the number straight through scrolls the wrong way, and
    /// a scroll that goes the wrong way still LOOKS like a working scroll, which
    /// is why this needs its own assertion rather than being inferred from the
    /// one above.
    /// </remarks>
    [Test]
    public void ANegativeW3CDelta_TurnsTheWheelPositively()
    {
        Perform("""{"type":"scroll","x":0,"y":0,"deltaY":-300}""").ShouldBeNull();

        _mouse.Received(1).Scroll(0, 3);
    }

    /// <summary>A sub-notch request still turns the wheel.</summary>
    /// <remarks>
    /// A caller asking for 30 CSS pixels means to scroll. Integer division gives
    /// zero notches, and dispatching a wheel event that turns nothing while
    /// answering 200 is the defect this file exists to close — so any non-zero
    /// request becomes at least one notch, in the direction asked.
    /// </remarks>
    [Test]
    public void ASubNotchScroll_IsRoundedUpRatherThanDroppedToNothing()
    {
        Perform("""{"type":"scroll","x":0,"y":0,"deltaY":30}""").ShouldBeNull();

        _mouse.Received(1).Scroll(0, -1);
    }

    /// <summary>Coordinates are viewport-relative, and the wheel moves there first.</summary>
    /// <remarks>
    /// A wheel event goes to the window UNDER THE CURSOR rather than the focused
    /// one, so the move is part of performing the scroll. The window is at
    /// (100, 200), so a viewport point of (10, 20) is (110, 220) on screen.
    /// </remarks>
    [Test]
    public void TheWheelTurnsAtTheViewportPoint()
    {
        Perform("""{"type":"scroll","x":10,"y":20,"deltaY":100}""").ShouldBeNull();

        _mouse.Received(1).MoveTo(110, 220);
    }

    /// <summary>A point outside the window is refused without scrolling.</summary>
    /// <remarks>
    /// THE CONTROL, and the same guard the pointer path learned the hard way: a
    /// coordinate treated as screen-relative puts real input into whatever
    /// application happens to be there. Asserting only the refusal would pass
    /// against a version that scrolled first and complained afterwards.
    /// </remarks>
    [Test]
    public void APointOutsideTheWindow_IsRefusedWithoutScrolling()
    {
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(false);

        Perform("""{"type":"scroll","x":10,"y":20,"deltaY":100}""")
            .ShouldNotBeNull()
            .Message.ShouldContain("outside the session window");

        _mouse.DidNotReceive().Scroll(Arg.Any<int>(), Arg.Any<int>());
    }

    /// <summary>A pause in a wheel source turns nothing and is not an error.</summary>
    /// <remarks>
    /// <b>The control for the step filter.</b> A wheel source may legitimately
    /// carry a pause to align its ticks with another device's. Refusing it would
    /// break a valid sequence; acting on it would dispatch a scroll the caller
    /// never asked for.
    /// </remarks>
    [Test]
    public void APauseInAWheelSource_IsNeitherAnErrorNorAScroll()
    {
        Perform("""{"type":"pause","duration":50}""").ShouldBeNull();

        _mouse.DidNotReceive().Scroll(Arg.Any<int>(), Arg.Any<int>());
    }

    /// <summary>A pointer source is left alone.</summary>
    /// <remarks>
    /// THE CONTROL FOR THE SOURCE FILTER. This runner is a peer of the pointer
    /// one, and a payload's pointer half is not its business — a version that
    /// scrolled for every source would turn the wheel on every gesture the suite
    /// sends.
    /// </remarks>
    [Test]
    public void APointerSource_IsNotTouched()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {"actions":[{"type":"pointer","id":"p","parameters":{"pointerType":"touch"},
              "actions":[{"type":"pointerMove","x":5,"y":5},
                         {"type":"scroll","x":0,"y":0,"deltaY":100}]}]}
            """);

        _runner.Perform(document.RootElement, Window).ShouldBeNull();

        _mouse.DidNotReceive().Scroll(Arg.Any<int>(), Arg.Any<int>());
    }

    private PointerRefusal? Perform(string step)
    {
        _mouse.ClearReceivedCalls();

        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {"actions":[{"type":"wheel","id":"wheel","actions":[{{step}}]}]}
            """);

        return _runner.Perform(document.RootElement, Window);
    }
}

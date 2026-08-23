using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The pen's button reaches Windows as the flag Windows names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The protocol layer cannot see this and neither can the suite.</b>
/// <c>PenButtonTests</c> proves a <c>button</c> in the payload survives as far as
/// <c>SyntheticContact.Button</c>, against a substituted injector. What that
/// value then becomes in <c>POINTER_PEN_INFO.penFlags</c> is the last step
/// before the syscall, and a wrong constant there fails silently — the injection
/// still succeeds, it just describes a different pen.
/// </para>
/// <para>
/// <b>The numbers are from the Windows headers, restated rather than
/// referenced</b>, which is the only way an assertion here can fail. Reading
/// them out of the class under test would make this agree with whatever that
/// class says, which is the definition of a test insensitive to its own subject.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class PenFlagsTests
{
    /// <summary>PEN_FLAG_BARREL, from winuser.h.</summary>
    private const uint Barrel = 0x00000001;

    /// <summary>PEN_FLAG_ERASER, from winuser.h.</summary>
    /// <remarks>
    /// Note the gap: <c>PEN_FLAG_INVERTED</c> is 0x2, so the eraser is 0x4 and
    /// not the 0x2 an unbroken sequence would suggest. That is exactly the sort
    /// of value a plausible guess gets wrong.
    /// </remarks>
    private const uint Eraser = 0x00000004;

    /// <summary>PEN_FLAG_NONE.</summary>
    private const uint None = 0x00000000;

    [Test]
    public void TheBarrel_IsTheBarrelFlag() =>
        SyntheticPointer.PenFlagsFor(SyntheticContactButton.Barrel).ShouldBe(Barrel);

    [Test]
    public void TheEraser_IsTheEraserFlag() =>
        SyntheticPointer.PenFlagsFor(SyntheticContactButton.Eraser).ShouldBe(Eraser);

    /// <summary>
    /// The tip carries no flags at all.
    /// </summary>
    /// <remarks>
    /// <b>The control, and the one that matters most.</b> Every pen test in the
    /// compatibility suite that currently PASSES presses the tip —
    /// <c>Pen_Click</c>, <c>Pen_Scroll_Vertical</c>, <c>Pen_DragAndDrop</c>. A
    /// mapping that returned a barrel for everything would satisfy the first
    /// assertion here and turn all of them into context-menu presses.
    /// </remarks>
    [Test]
    public void TheTip_CarriesNoFlags() =>
        SyntheticPointer.PenFlagsFor(SyntheticContactButton.Tip).ShouldBe(None);

    /// <summary>The three are distinct, so none can be silently conflated.</summary>
    /// <remarks>
    /// Stated separately because the assertions above are each satisfied by a
    /// mapping that happens to be right for one input, and "barrel and eraser
    /// are the same flag" is a plausible transcription slip that all three would
    /// otherwise have to catch individually.
    /// </remarks>
    [Test]
    public void TheThreeButtons_MapToThreeDifferentFlags()
    {
        uint tip = SyntheticPointer.PenFlagsFor(SyntheticContactButton.Tip);
        uint barrel = SyntheticPointer.PenFlagsFor(SyntheticContactButton.Barrel);
        uint eraser = SyntheticPointer.PenFlagsFor(SyntheticContactButton.Eraser);

        new[] { tip, barrel, eraser }.ShouldBeUnique();
    }
}

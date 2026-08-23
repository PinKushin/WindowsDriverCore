using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// An injected contact declares itself the PRIMARY pointer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows only promotes the primary pointer.</b> Gesture recognition —
/// press-and-hold, double-tap, the touch-to-mouse promotion legacy controls rely
/// on — is driven from the pointer marked <c>POINTER_FLAG_PRIMARY</c>. A contact
/// injected without it is delivered as raw pointer input and never becomes a
/// gesture, which is indistinguishable from "the touch did not arrive" unless
/// you know to look for the flag.
/// </para>
/// <para>
/// <b>MEASURED, and the two halves of one test disagree.</b> Inside
/// <c>TouchLongTap</c> on the guest:
/// </para>
/// <code>
/// 02:50:35.471  click button 2 -> (616,324)          &lt;- mouse right-click
/// 02:50:38.489  find Name='Delete' -> 1 match(es)    &lt;- context menu appeared
///
/// 02:50:48.313  POST /touch/longclick -> 200 (1099 ms)
/// 02:50:51.344  find Name='Delete' -> 0 match(es)    &lt;- no menu; 38 retries, 404
/// </code>
/// <para>
/// Same element, same run, seconds apart. A 1.1-second held contact produced no
/// context menu where a mouse right-click produced one immediately. And
/// <c>TouchLongTap</c> has failed in all nine measured runs — it is not a
/// regression, it never worked.
/// </para>
/// <para>
/// <b>Not asserted here:</b> that the flag fixes the test. This states only that
/// the flag reaches the injector, which is the last thing checkable without an
/// application in front of it.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class PrimaryPointerTests
{
    /// <summary>POINTER_FLAG_PRIMARY, from winuser.h.</summary>
    /// <remarks>
    /// Restated rather than read from the class under test. Taking the number
    /// from the code being tested would make this agree with whatever that code
    /// says, which is the definition of a test insensitive to its subject.
    /// </remarks>
    private const uint Primary = 0x00002000;

    [Test]
    public void ADownContact_IsPrimary() =>
        (SyntheticPointer.FlagsForPhase(SyntheticContactPhase.Down) & Primary)
            .ShouldBe(Primary, "a contact that is not primary never becomes a gesture");

    [Test]
    public void AHeldContact_StaysPrimary() =>
        (SyntheticPointer.FlagsForPhase(SyntheticContactPhase.Update) & Primary)
            .ShouldBe(Primary, "a press-and-hold is a sequence of updates, not one down");

    [Test]
    public void TheLift_IsPrimaryToo() =>
        (SyntheticPointer.FlagsForPhase(SyntheticContactPhase.Up) & Primary)
            .ShouldBe(Primary, "the gesture is not complete until the primary pointer lifts");

    /// <summary>
    /// The phase bits survive, so PRIMARY is added rather than substituted.
    /// </summary>
    /// <remarks>
    /// <b>The control.</b> Returning PRIMARY alone would satisfy all three tests
    /// above and stop every gesture working entirely — down, update and up would
    /// become indistinguishable to the receiver.
    /// </remarks>
    [Test]
    public void TheThreePhases_RemainDistinct()
    {
        uint down = SyntheticPointer.FlagsForPhase(SyntheticContactPhase.Down);
        uint update = SyntheticPointer.FlagsForPhase(SyntheticContactPhase.Update);
        uint up = SyntheticPointer.FlagsForPhase(SyntheticContactPhase.Up);

        new[] { down, update, up }.ShouldBeUnique();

        // POINTER_FLAG_UP, and it must NOT carry INCONTACT - the contact is gone.
        (up & 0x00000004u).ShouldBe(0u, "a lifted contact is not in contact");
        (down & 0x00000004u).ShouldNotBe(0u, "a pressed contact is");
    }
}

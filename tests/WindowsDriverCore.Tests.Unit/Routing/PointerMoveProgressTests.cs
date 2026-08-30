using System;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Routing;

namespace WindowsDriverCore.Tests.Unit.Routing;

/// <summary>
/// How far along a pointer move is at a given moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists for, measured on the guest 2026-08-30.</b> The
/// compatibility suite's own gesture — 200 px over 500 ms onto Alarms &amp;
/// Clock's <c>MinuteLoopingSelector</c> — was run ten times in one session. The
/// identical request moved the selector by anywhere from <b>5 to 15 items</b>,
/// and a down/up pair cancelled exactly once in ten:
/// </para>
/// <code>
/// net drift per round : 0, 1, 1, -4, -3, -9, -7, 8, -3, -3 items
/// </code>
/// <para>
/// The cause is that position followed the FRAME COUNTER while the sleep
/// followed the CLOCK. Frames sleep to a start-relative deadline, which keeps
/// the total duration right — but when a sleep overshoots, the next deadlines
/// are already past and several frames fire with no sleep between them.
/// Index-driven interpolation gives each the same position step across almost no
/// time, so a target measuring velocity sees a spike. A LoopingSelector flings
/// on velocity, so a noisy velocity is a noisy distance.
/// </para>
/// <para>
/// <b>Why this is a pure function and not a timing test.</b> The property that
/// matters is that position is a function of ELAPSED TIME ALONE. Expressed that
/// way it is decidable without a clock, a scheduler, or a real gesture — and a
/// test that had to observe jitter to prove the point would be measuring the
/// scheduler rather than the driver.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PointerMoveProgressTests
{
    private static readonly TimeSpan HalfSecond = TimeSpan.FromMilliseconds(500);

    /// <summary>Progress is the fraction of the duration that has passed.</summary>
    [Test]
    public void HalfwayThroughTheDuration_IsHalfway() =>
        PointerActionRunner.ProgressAt(TimeSpan.FromMilliseconds(250), HalfSecond, frame: 1, frames: 60)
            .ShouldBe(0.5);

    /// <summary>The frame index does not move the pointer.</summary>
    /// <remarks>
    /// <b>THE TEST THAT IS THE FIX.</b> Two frames emitted at the same instant —
    /// which is exactly what a catch-up burst is — must report the same position.
    /// Index-driven interpolation gives them different positions across no time
    /// at all, which is the velocity spike that made the gesture's distance
    /// random.
    /// </remarks>
    [Test]
    public void TwoFramesAtTheSameInstant_ReportTheSamePosition()
    {
        double early = PointerActionRunner.ProgressAt(
            TimeSpan.FromMilliseconds(100), HalfSecond, frame: 12, frames: 60);

        double late = PointerActionRunner.ProgressAt(
            TimeSpan.FromMilliseconds(100), HalfSecond, frame: 30, frames: 60);

        late.ShouldBe(early, "position must follow the clock, not the frame counter");
    }

    /// <summary>A move that has overrun its duration is complete, not beyond it.</summary>
    /// <remarks>
    /// Without the clamp a late final frame lands PAST the target, which is an
    /// overshoot the client never asked for and a position outside the element
    /// the origin was resolved from.
    /// </remarks>
    [Test]
    public void PastTheDuration_IsClampedToComplete() =>
        PointerActionRunner.ProgressAt(TimeSpan.FromMilliseconds(900), HalfSecond, frame: 60, frames: 60)
            .ShouldBe(1.0);

    /// <summary>The start of a move is the start.</summary>
    [Test]
    public void AtTheStart_IsZero() =>
        PointerActionRunner.ProgressAt(TimeSpan.Zero, HalfSecond, frame: 0, frames: 60)
            .ShouldBe(0.0);

    /// <summary>With no duration stated, the frame index is all there is.</summary>
    /// <remarks>
    /// <b>The control, and it is not decoration.</b> A client may state no
    /// duration at all, and then there is no clock to follow — every frame would
    /// report elapsed ≈ 0 and the pointer would never leave its origin. The
    /// index path must survive, which is the half of this that a
    /// "always use the clock" change would silently destroy.
    /// </remarks>
    [Test]
    public void WithNoDuration_ProgressFollowsTheFrameIndex() =>
        PointerActionRunner.ProgressAt(TimeSpan.Zero, TimeSpan.Zero, frame: 3, frames: 10)
            .ShouldBe(0.3);
}

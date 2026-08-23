using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A mouse drag walks its path instead of teleporting.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED as <c>MouseDownMoveUp</c> failing on the guest with
/// <i>"Expected any value except {X=290,Y=154}. Actual: {X=290,Y=154}"</i></b> —
/// the window sat exactly where it started. The test presses on the title bar,
/// moves by 100, releases, and asserts the window moved.
/// </para>
/// <para>
/// A window manager samples the pointer on its own message loop, so a drag
/// delivered as one absolute jump is not a gesture it can follow. This is the
/// same defect already fixed for touch and pen; it arrived separately here
/// because mouse input goes through <c>SendInput</c> rather than the
/// pointer-injection API and shares none of that code.
/// </para>
/// <para>
/// <b>The PATH is asserted rather than the injection.</b> Asserting on
/// synthesized mouse input needs a real desktop and moves the operator's
/// cursor; the arithmetic that decides where the pointer goes does not.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class MouseDragPathTests
{
    [Test]
    public void ADrag_IsMoreThanOneStep()
    {
        SendInputPointer.PathBetween(100, 100, 200, 200).Count.ShouldBeGreaterThan(
            1, "one absolute jump is the teleport the window manager cannot follow");
    }

    /// <summary>The path ends exactly where the caller asked.</summary>
    /// <remarks>
    /// Integer division truncates, so a path that walks and then stops a pixel
    /// short would satisfy every other assertion here and drop the window in the
    /// wrong place.
    /// </remarks>
    [Test]
    public void ThePath_EndsOnTheDestination()
    {
        SendInputPointer.PathBetween(100, 100, 237, 419)[^1].ShouldBe((237, 419));
    }

    /// <summary>It goes the right way, and monotonically.</summary>
    /// <remarks>
    /// <b>The control.</b> A path that ends correctly could still wander — and a
    /// drag that passes back over its origin can drop what it is carrying. This
    /// is also what would catch a sign error, which "ends on the destination"
    /// would not.
    /// </remarks>
    [Test]
    public void ThePath_MovesTowardsTheDestinationThroughout()
    {
        IReadOnlyList<(int X, int Y)> path = SendInputPointer.PathBetween(0, 0, 100, -50);

        for (int step = 1; step < path.Count; step++)
        {
            path[step].X.ShouldBeGreaterThanOrEqualTo(path[step - 1].X);
            path[step].Y.ShouldBeLessThanOrEqualTo(path[step - 1].Y);
        }
    }

    /// <summary>A drag that goes nowhere still produces a path, not an empty one.</summary>
    /// <remarks>
    /// The degenerate case. An empty path would make <c>MoveTo</c> return true
    /// having sent nothing, which reports a move that did not happen.
    /// </remarks>
    [Test]
    public void ADragToWhereItAlreadyIs_StillHasSteps()
    {
        IReadOnlyList<(int X, int Y)> path = SendInputPointer.PathBetween(50, 60, 50, 60);

        path.ShouldNotBeEmpty();
        path[^1].ShouldBe((50, 60));
    }
}

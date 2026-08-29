using System;
using System.Collections.Generic;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Routing;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// A drag with no stated duration is paced by SPEED, not by a fixed time.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED on the guest.</b> <c>/touch/scroll</c> of -55 px against Alarms
/// &amp; Clock's minute selector, three attempts per row:
/// </para>
/// <code>
///   WinAppDriver, -55 px      hid "00"  3/3     &lt;- the reference
///   ours, -55 px /  60 ms     hid "00"  3/3
///   ours, -55 px / 150 ms     hid "00"  3/3
///   ours, -55 px / 300 ms     hid "00"  0/3     &lt;- as shipped
/// </code>
/// <para>
/// A <c>LoopingSelector</c> either flings or merely drags, and it decides on
/// VELOCITY. 55 px over the shipped 300 ms is 183 px/s and does not fling, which
/// is why <c>TouchScrollOnElement_Vertical</c> failed outright rather than
/// flapping.
/// </para>
/// <para>
/// A fixed duration makes a short gesture slow and a long one fast — so the one
/// thing the application reacts to was the one thing left uncontrolled.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DragSpeedTests
{
    private ISyntheticPointer _injector = null!;
    private PointerActionRunner _runner = null!;

    [SetUp]
    public void Arrange()
    {
        _injector = Substitute.For<ISyntheticPointer>();
        _injector.CanInject(Arg.Any<SyntheticPointerKind>()).Returns(true);
        _injector.Inject(Arg.Any<IReadOnlyList<SyntheticContact>>()).Returns(true);

        IWindowLocator windows = Substitute.For<IWindowLocator>();
        windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);
        windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(0, 0, 1600, 1200));

        _runner = new PointerActionRunner(
            _injector, Substitute.For<IElementInspector>(), windows);
    }

    /// <summary>A longer drag takes proportionally longer.</summary>
    /// <remarks>
    /// The defining property of a speed. Under the fixed duration this replaced,
    /// both of these took exactly 300 ms and the longer one was therefore four
    /// times faster — which is the defect, stated as a test.
    /// </remarks>
    [Test]
    public void AFourTimesLongerDrag_TakesAboutFourTimesAsLong()
    {
        TimeSpan shortDrag = TimeOf(() => _runner.Drag(100, 100, 100, 300));
        TimeSpan longDrag = TimeOf(() => _runner.Drag(100, 100, 100, 900));

        double ratio = longDrag.TotalMilliseconds / shortDrag.TotalMilliseconds;

        // Generous bounds: the assertion is that it SCALES, not that the
        // scheduler is precise. A fixed duration gives a ratio of 1.
        ratio.ShouldBeGreaterThan(2.5);
        ratio.ShouldBeLessThan(6.0);
    }

    /// <summary>
    /// The speed is fast enough to fling.
    /// </summary>
    /// <remarks>
    /// Stated against the measurement rather than against the constant: 55 px in
    /// 300 ms was measured NOT flinging, 55 px in 150 ms was. Reading the
    /// driver's own constant here would make the test agree with whatever the
    /// code says, which is the definition of an assertion insensitive to its
    /// subject.
    /// </remarks>
    [Test]
    public void TheSuitesOwnScroll_IsFasterThanTheSpeedMeasuredFailing()
    {
        // The suite's TouchScrollOnElement_Vertical scrolls exactly this far.
        TimeSpan taken = TimeOf(() => _runner.Drag(100, 100, 100, 45));

        taken.ShouldBeLessThan(
            TimeSpan.FromMilliseconds(150),
            "55 px over 150 ms flung the selector 3/3 and over 300 ms flung it 0/3");
    }

    /// <summary>
    /// A tiny drag is not compressed below one frame separation.
    /// </summary>
    /// <remarks>
    /// <b>The control.</b> Speed alone would give a 5 px drag about 5 ms, and
    /// below roughly 2 ms per frame the frames are coalesced into a single jump
    /// and the gesture stops existing — measured when the separation constant
    /// was set. So the floor is not tidiness; without it the fix would break the
    /// smallest gestures to help the large ones.
    /// </remarks>
    [Test]
    public void AVeryShortDrag_StillGetsAWholeFrameSeparation()
    {
        TimeSpan taken = TimeOf(() => _runner.Drag(100, 100, 100, 95));

        taken.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(35));
    }

    private static TimeSpan TimeOf(Func<PointerRefusal?> act)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        act().ShouldBeNull("the gesture must run for its duration to be measurable");
        clock.Stop();
        return clock.Elapsed;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Routing;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// A move's frame RATE stays near a digitiser's, whatever its duration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observed by the owner watching the drag tests run:</b> "all our moves are
/// kinda jittery while i think winappdrivers is actually smooth". The arithmetic
/// agrees and does not need a screen to confirm it — a move was broken into a
/// FIXED ten frames regardless of how long the caller said it should take, so a
/// one-second <c>/actions</c> drag emitted one frame every hundred milliseconds.
/// A real digitiser reports at roughly 100–133 Hz. Ten hertz is not a slower
/// gesture; it is a different signal.
/// </para>
/// <para>
/// <b>Why this is capability and not cosmetics.</b> An application that
/// distinguishes a flick from a drag samples pointer velocity, and a sample every
/// 100 ms is below what any gesture recogniser is built to see. The same
/// reasoning already in <c>Move</c> — "a drag is the PATH and not the
/// endpoints" — does not stop at two frames instead of one; it argues for a rate,
/// and the rate was never derived.
/// </para>
/// <para>
/// <b>Nothing in the compatibility suite asserts this.</b> Its drag tests check
/// that the window moved, which ten frames already achieves. So this fixture is
/// the only thing that will notice if the rate regresses, and a score is not
/// evidence either way.
/// </para>
/// <para>
/// <b>The runner is driven directly rather than over HTTP.</b> The claim is about
/// what <see cref="PointerActionRunner"/> emits, and a route in front of it would
/// add a host boot to every case without changing what is measured. The injector
/// is a substitute for the usual reason — this project has twice sent real touch
/// into the owner's desktop from a test.
/// </para>
/// </remarks>
[TestFixture]
public sealed class GesturePacingTests
{
    private const nint Window = 0x1234;

    /// <summary>The rate a real touch digitiser reports at, in hertz.</summary>
    /// <remarks>
    /// Used to state the EXPECTATION independently of the constant the runner
    /// uses. Deriving the assertion from the implementation's own number would
    /// make this test agree with whatever the code does, which is the definition
    /// of an experiment insensitive to its manipulation.
    /// </remarks>
    private const int DigitiserHertz = 100;

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

        // A viewport coordinate is WINDOW-relative, so the runner needs the
        // window placed before it can turn one into a screen point. The origin
        // is zero here on purpose: the subject is how many frames appear and
        // where the last one lands, and a non-zero offset would put arithmetic
        // that is tested elsewhere between the request and the assertion.
        windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(0, 0, 800, 600));

        _runner = new PointerActionRunner(
            _injector, Substitute.For<IElementInspector>(), windows);
    }

    /// <summary>A long move is sampled like a digitiser, not like a slideshow.</summary>
    /// <remarks>
    /// <para>
    /// <b>The condition is chosen so correct and broken differ.</b> At 500 ms a
    /// fixed ten frames is 20 ms apart and a rate-based one is about 100 — an
    /// order of magnitude, far above any pacing jitter. A 50 ms move would have
    /// been useless here: both implementations emit ten and the test could not
    /// fail.
    /// </para>
    /// <para>
    /// The floor is stated as a rate rather than a count so that changing the
    /// frame interval for a good reason does not require editing an expectation
    /// that means nothing on its own.
    /// </para>
    /// </remarks>
    [Test]
    public void ALongMove_IsSampledAtRoughlyADigitisersRate()
    {
        TimeSpan requested = TimeSpan.FromMilliseconds(500);

        Perform(Drag(toX: 400, toY: 400, duration: requested));

        int expected = (int)(requested.TotalSeconds * DigitiserHertz);

        UpdateFrames().Count.ShouldBeGreaterThanOrEqualTo(
            expected,
            $"a {requested.TotalMilliseconds} ms move sampled below {DigitiserHertz} Hz is a " +
            "slideshow, not a gesture - a recogniser watching for velocity sees nothing in it");
    }

    /// <summary>
    /// A short move keeps a usable path instead of collapsing to a jump.
    /// </summary>
    /// <remarks>
    /// <b>The control for the test above, and it fails the obvious wrong fix.</b>
    /// Deriving the count from the duration alone — <c>duration / interval</c> —
    /// satisfies the rate assertion and quietly reduces a 20 ms move to two
    /// frames, or an instant one to none at all. That is the teleport this path
    /// was fixed for once already, arriving through the change meant to smooth it.
    /// </remarks>
    [Test]
    public void AShortMove_StillWalksItsPath()
    {
        Perform(Drag(toX: 400, toY: 400, duration: TimeSpan.FromMilliseconds(20)));

        UpdateFrames().Count.ShouldBeGreaterThanOrEqualTo(
            10,
            "a brief move is still a path; collapsing it to a couple of frames " +
            "reintroduces the single-jump teleport measured on the guest");
    }

    /// <summary>
    /// However many frames there are, the last one is where the caller asked.
    /// </summary>
    /// <remarks>
    /// The second control. Interpolation divides by the frame count, so a change
    /// to that count is a change to the arithmetic - and a path that stops one
    /// frame short still satisfies both assertions above while leaving the window
    /// in the wrong place.
    /// </remarks>
    [Test]
    public void TheFinalFrame_LandsExactlyOnTheTarget()
    {
        Perform(Drag(toX: 400, toY: 400, duration: TimeSpan.FromMilliseconds(500)));

        SyntheticContact last = UpdateFrames()[^1];

        last.X.ShouldBe(400);
        last.Y.ShouldBe(400);
    }

    /// <summary>Every emitted contact update, in order.</summary>
    private List<SyntheticContact> UpdateFrames() =>
        _injector.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ISyntheticPointer.Inject))
            .Select(call => (IReadOnlyList<SyntheticContact>)call.GetArguments()[0]!)
            .SelectMany(contacts => contacts)
            .Where(contact => contact.Phase == SyntheticContactPhase.Update)
            .ToList();

    private void Perform(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);

        PointerRefusal? refusal = _runner.Perform(document.RootElement, Window);

        refusal.ShouldBeNull("the gesture must be performed for its frames to be countable");
    }

    /// <summary>
    /// Press, move to a point over the stated duration, lift.
    /// </summary>
    /// <remarks>
    /// A viewport origin, so the run needs no element lookup - the subject is
    /// pacing, and resolving an element would put a second mechanism inside the
    /// measurement.
    /// </remarks>
    private static string Drag(int toX, int toY, TimeSpan duration) =>
        $$"""
        {
          "actions": [{
            "type": "pointer",
            "id": "finger",
            "parameters": { "pointerType": "touch" },
            "actions": [
              { "type": "pointerMove", "origin": "viewport", "x": 0, "y": 0 },
              { "type": "pointerDown", "button": 0 },
              { "type": "pointerMove", "origin": "viewport", "x": {{toX}}, "y": {{toY}},
                "duration": {{(int)duration.TotalMilliseconds}} },
              { "type": "pointerUp", "button": 0 }
            ]
          }]
        }
        """;
}

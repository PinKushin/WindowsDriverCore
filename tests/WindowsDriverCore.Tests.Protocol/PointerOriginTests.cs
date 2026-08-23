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
/// The two coordinate origins agree about where a point is.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the arithmetic half of a failure that has already cost one guest
/// run.</b> <c>Pen_Click_OriginPointer</c> and <c>Touch_Click_OriginPointer</c>
/// fail against this driver and pass against WinAppDriver. A fix was written,
/// merged, measured at 262 → 259 and reverted at <c>9f768cb</c>; the note in
/// <c>docs/LIMITATIONS.md</c> records that the next attempt must find where the
/// coordinate comes from BEFORE changing how it is computed.
/// </para>
/// <para>
/// <b>So this asks the question without a desktop.</b> Whether a pointer origin
/// and a viewport origin land on the same screen point is pure arithmetic over a
/// window rectangle — no application, no guest, no injection. A guest run
/// answers it in twenty-five minutes and conflates it with everything else in
/// the suite; this answers it in a second and conflates it with nothing.
/// </para>
/// <para>
/// <b>Why they must agree.</b> W3C starts a fresh pointer at (0,0) of the
/// VIEWPORT, and in this driver the viewport is the window — the same rule the
/// <c>viewport</c> origin already follows, measured and recorded in
/// <c>PointerStaysInsideTheWindowTests</c>. The suite feeds the same
/// window-relative <c>element.Location</c> into both forms and expects both to
/// hit the element, so a driver where they disagree is wrong on one of them. The
/// viewport form is the one already passing on the guest, which makes it the
/// reference here rather than an equal partner.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PointerOriginTests
{
    private const nint Window = 0x4321;

    /// <summary>Where the session window sits on the desktop.</summary>
    /// <remarks>
    /// Deliberately NOT at the desktop corner. An origin bug that adds nothing is
    /// invisible against a window at (0,0) — correct and broken would predict the
    /// same observation, which is the defining property of a test that cannot
    /// fail.
    /// </remarks>
    private static readonly WindowBounds Bounds = new(208, 114, 900, 700);

    private ISyntheticPointer _injector = null!;
    private IWindowLocator _windows = null!;
    private IElementInspector _elements = null!;
    private PointerActionRunner _runner = null!;

    [SetUp]
    public void Arrange()
    {
        _injector = Substitute.For<ISyntheticPointer>();
        _injector.CanInject(Arg.Any<SyntheticPointerKind>()).Returns(true);
        _injector.Inject(Arg.Any<IReadOnlyList<SyntheticContact>>()).Returns(true);

        _windows = Substitute.For<IWindowLocator>();
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);
        _windows.GetBounds(Arg.Any<nint>()).Returns(Bounds);

        // An unconfigured substitute answers Outcome=Read with a null Value,
        // which is a state the real inspector never produces - so an element
        // origin would silently read as a successful lookup at (0,0). Stated
        // explicitly here rather than left to the default.
        _elements = Substitute.For<IElementInspector>();
        _elements.ScreenBounds(Arg.Any<nint>(), Arg.Any<string>())
            .Returns(new ElementRead<ElementBounds>(default, ElementReadOutcome.NoSuchWindow));

        _runner = new PointerActionRunner(_injector, _elements, _windows);
    }

    /// <summary>
    /// A fresh pointer starts at the viewport origin, so both forms agree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The suite's own gesture, reduced to its coordinates.
    /// <c>Touch_Click_OriginPointer</c> reads <c>element.Location</c> — which
    /// this driver answers window-relative — and feeds it as a POINTER-origin
    /// delta from a pointer it has not moved yet, commenting the starting values
    /// as "Initial x coordinate". <c>Touch_Click_OriginViewport</c> feeds the
    /// identical numbers as a VIEWPORT origin. Both must reach the element.
    /// </para>
    /// <para>
    /// Stated as an equality between the two forms rather than as a literal
    /// screen point, because the literal is only correct while the fixture's
    /// window rectangle is: an assertion on <c>(309, 147)</c> would need editing
    /// for a reason that has nothing to do with the behaviour.
    /// </para>
    /// </remarks>
    [Test]
    public void APointerOrigin_ReachesTheSamePointAsAViewportOrigin()
    {
        SyntheticContact viaViewport = OnlyPress(Tap("viewport", 101, 33));

        // FRESH, AND NOW IT HAS TO BE SAID OUT LOUD. The claim is about a
        // pointer that has not moved yet, and since a pointer's position began
        // surviving between requests the viewport gesture above leaves one
        // behind. Without this the second tap would correctly resume from
        // (309,147) and the test would be comparing a fresh pointer against a
        // carried one - two different questions.
        _runner.ForgetContacts();

        SyntheticContact viaPointer = OnlyPress(Tap("pointer", 101, 33));

        (viaPointer.X, viaPointer.Y).ShouldBe(
            (viaViewport.X, viaViewport.Y),
            "a fresh pointer sits at the viewport origin, so the same offset from " +
            "either must name the same pixel - the suite feeds element.Location to both");
    }

    /// <summary>
    /// A second pointer-origin move is relative to where the first one ended.
    /// </summary>
    /// <remarks>
    /// <b>The control that a one-move test cannot provide.</b> Initialising the
    /// pointer correctly and then failing to carry its position forward would
    /// satisfy the test above and still break the suite, because
    /// <c>Touch_Click_OriginPointer</c> performs two gestures and computes the
    /// second as a genuine delta — <c>alarm.Location.X - worldClock.Location.X</c>
    /// — from wherever the first one left the pointer.
    /// </remarks>
    [Test]
    public void ASecondPointerMove_ContinuesFromWhereTheFirstEnded()
    {
        SyntheticContact landed = OnlyPress(
            """
            { "type": "pointerMove", "origin": "pointer", "x": 101, "y": 33 },
            { "type": "pointerMove", "origin": "pointer", "x": 40, "y": 12 },
            { "type": "pointerDown", "button": 0 },
            { "type": "pointerUp", "button": 0 }
            """);

        (landed.X, landed.Y).ShouldBe(
            (Bounds.X + 101 + 40, Bounds.Y + 33 + 12),
            "the second offset is measured from the first move's destination");
    }

    /// <summary>
    /// A dead window reports the ELEMENT fault, not the placement fault.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE REGRESSION GUARD, and it is the exact three tests the previous
    /// attempt cost.</b> That change resolved the viewport origin EAGERLY, at the
    /// top of every pointer source, before knowing whether the sequence contained
    /// a pointer-origin move at all. The suite's <c>ActionsError_NoSuchWindow</c>
    /// sends a move against an ELEMENT origin on an orphaned session and asserts
    /// <i>"Currently selected window has been closed"</i> — but on a dead window
    /// the eager placement call fails first and answers <i>"The session window
    /// could not be placed, so a viewport coordinate has no meaning"</i> instead.
    /// A correct fault, arriving in place of the one the client is waiting for.
    /// </para>
    /// <para>
    /// <b>Written as the suite's own shape rather than a convenient one.</b> An
    /// earlier draft of this guard used a bare down-and-up, which is not
    /// something the suite ever sends — it would have passed against the eager
    /// version too, and guarded nothing. The distinguishing observation is that
    /// the refusal carries an <c>ElementOutcome</c>: that is what the route layer
    /// translates into the suite's sentence, and the placement refusal has none.
    /// </para>
    /// </remarks>
    [Test]
    public void AnElementOriginOnADeadWindow_FaultsAboutTheElement()
    {
        _windows.GetBounds(Arg.Any<nint>()).Returns((WindowBounds?)null);

        PointerRefusal? refusal = Run(
            """
            { "type": "pointerMove", "origin": { "ELEMENT": "orphan" }, "x": 0, "y": 0 },
            { "type": "pointerDown", "button": 0 },
            { "type": "pointerUp", "button": 0 }
            """);

        refusal.ShouldNotBeNull();
        refusal.ElementOutcome.ShouldNotBeNull(
            "the caller asked about an element, so the fault must be about the " +
            "element - a placement fault here is the message ActionsError_NoSuchWindow " +
            "does not expect, which is what cost three tests last time");
    }

    /// <summary>
    /// A pointer-origin move DOES need the window, and says so.
    /// </summary>
    /// <remarks>
    /// The bystander for the guard above. Made lazy carelessly, the resolution
    /// could be skipped entirely — every sequence would then pass the test above
    /// while silently computing desktop coordinates again, which is the original
    /// defect wearing the fix's name.
    /// </remarks>
    [Test]
    public void APointerOriginMove_StillRefusesWhenTheWindowCannotBePlaced()
    {
        _windows.GetBounds(Arg.Any<nint>()).Returns((WindowBounds?)null);

        PointerRefusal? refusal = Run(
            """
            { "type": "pointerMove", "origin": "pointer", "x": 101, "y": 33 },
            { "type": "pointerDown", "button": 0 },
            { "type": "pointerUp", "button": 0 }
            """);

        refusal.ShouldNotBeNull(
            "an offset from the viewport origin is meaningless without the viewport");
    }

    /// <summary>
    /// The pointer stays where the previous REQUEST left it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured on the guest, and only visible because the refusal started
    /// naming the window.</b> After the viewport anchor was fixed,
    /// <c>Touch_Click_OriginPointer</c> still failed — with
    /// <i>"(115,87) is outside the application window at (208,87) 816x641"</i>.
    /// The window's left edge is 208 and the point is 115, so the offset applied
    /// was −93 with a zero Y.
    /// </para>
    /// <para>
    /// That is the test's SECOND gesture. It computes
    /// <c>alarm.Location.X - worldClock.Location.X</c> — a genuine delta from
    /// where the first gesture left the pointer — and sends it in a separate
    /// <c>PerformActions</c> call. Each request started a fresh
    /// <c>PerformSource</c> with the pointer back at the viewport origin, so the
    /// delta was measured from the wrong place and landed left of the window.
    /// </para>
    /// <para>
    /// W3C is explicit that input state belongs to the SESSION and persists
    /// between <c>/actions</c> commands until it is released. A per-request
    /// pointer is not a smaller version of that; it is a different model.
    /// </para>
    /// </remarks>
    [Test]
    public void APointerOrigin_ContinuesFromWhereThePreviousRequestLeftIt()
    {
        // First request: from the viewport origin, out to +101,+33.
        OnlyPress(Tap("pointer", 101, 33));

        // Second request, the suite's own shape: a delta from there, and the
        // guest's actual numbers - back and to the left.
        SyntheticContact second = OnlyPress(Tap("pointer", -93, 0));

        (second.X, second.Y).ShouldBe(
            (Bounds.X + 101 - 93, Bounds.Y + 33),
            "the second request's offset is measured from where the first one " +
            "left the pointer, not from the viewport origin again");
    }

    /// <summary>A different window does not inherit another's pointer.</summary>
    /// <remarks>
    /// <b>The control.</b> Remembering the position in one shared slot would
    /// satisfy the test above and let a gesture in one session start from
    /// wherever an unrelated session's gesture ended — a wrong coordinate
    /// delivered into a real application, which is the failure this whole path
    /// has a guard for.
    /// </remarks>
    [Test]
    public void AnotherWindow_StartsAtItsOwnViewportOrigin()
    {
        OnlyPress(Tap("pointer", 101, 33));

        SyntheticContact elsewhere = OnlyPress(Tap("pointer", 10, 20), window: 0x7777);

        (elsewhere.X, elsewhere.Y).ShouldBe(
            (Bounds.X + 10, Bounds.Y + 20),
            "a window with no pointer history starts at its own viewport origin");
    }

    /// <summary>
    /// A refused point names the window it was measured against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because a bare coordinate is unfalsifiable, and this has now cost two
    /// investigations.</b> The two <c>*_OriginPointer</c> tests failed with
    /// "(101,33) is outside", the anchor was corrected, and they failed with
    /// "(115,87) is outside". Neither message says where the window was, so both
    /// times the next step was a guess about it — and one of those guesses cost a
    /// guest run and three tests.
    /// </para>
    /// <para>
    /// With the rectangle present the two possible causes separate by arithmetic:
    /// a point INSIDE the reported rectangle means the ownership check refuses
    /// something the window contains, and a point OUTSIDE means the anchor and
    /// the rectangle disagree about the coordinate space. Those need opposite
    /// fixes.
    /// </para>
    /// <para>
    /// <b>Asserted on the string the client actually receives</b>, not on a log
    /// call. A diagnostic that exists only in the transcript is not available to
    /// whoever is reading a test failure, which is the audience that needs it.
    /// </para>
    /// </remarks>
    [Test]
    public void ARefusedPoint_ReportsTheWindowItWasMeasuredAgainst()
    {
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(false);

        PointerRefusal? refusal = Run(Tap("viewport", 101, 33));

        refusal.ShouldNotBeNull();
        refusal.Message.ShouldContain($"({Bounds.X},{Bounds.Y})");
        refusal.Message.ShouldContain($"{Bounds.Width}x{Bounds.Height}");
    }

    /// <summary>The contact injected by the press, which is the point that matters.</summary>
    /// <remarks>
    /// The DOWN rather than the last move: a gesture that walks to the right place
    /// and then presses somewhere else is exactly the defect being chased, and
    /// reading the move would not see it.
    /// </remarks>
    private SyntheticContact OnlyPress(string steps, nint window = Window)
    {
        Run(steps, window).ShouldBeNull("the gesture must run for its contact to be readable");

        List<SyntheticContact> pressed = _injector.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ISyntheticPointer.Inject))
            .Select(call => (IReadOnlyList<SyntheticContact>)call.GetArguments()[0]!)
            .SelectMany(contacts => contacts)
            .Where(contact => contact.Phase == SyntheticContactPhase.Down)
            .ToList();

        pressed.Count.ShouldBe(1, "one press per gesture");
        return pressed[0];
    }

    private PointerRefusal? Run(string steps, nint window = Window)
    {
        _injector.ClearReceivedCalls();

        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "actions": [{
                "type": "pointer",
                "id": "finger",
                "parameters": { "pointerType": "touch" },
                "actions": [ {{steps}} ]
              }]
            }
            """);

        return _runner.Perform(document.RootElement, window);
    }

    private static string Tap(string origin, int x, int y) =>
        $$"""
        { "type": "pointerMove", "origin": "{{origin}}", "x": {{x}}, "y": {{y}} },
        { "type": "pointerDown", "button": 0 },
        { "type": "pointerUp", "button": 0 }
        """;
}

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
/// <c>width</c>, <c>height</c> and <c>twist</c> reach the contact.
/// </summary>
/// <remarks>
/// <para>
/// <b>All three were VALIDATED and then thrown away</b> — the exact shape of the
/// <c>/touch/flick speed</c> defect that started this audit, and arguably worse,
/// because the validation makes the route look complete. <c>ActionRoutes</c>
/// checks each against the compatibility suite's own error messages, with two
/// dedicated tests pinning the width ones character for character, and then
/// <c>PointerActionRunner</c> built a contact that carried none of them.
/// </para>
/// <para>
/// So a client asking for a 40x40 fingertip got the hardcoded 4x4 box, and a
/// client rotating a pen got no rotation at all. Both were answered 200.
/// </para>
/// <para>
/// <b>Found by the by-parameter lens asking a different question:</b> not "is
/// this key read" — all three were — but "is what was read ever USED". A
/// parameter parsed into a local and dropped is invisible to every other check
/// in this project, including the one that found <c>speed</c>.
/// </para>
/// <para>
/// What this fixture establishes is that the values survive the protocol layer
/// and reach the injector. Whether Windows then delivers a 40 px contact that an
/// application distinguishes from a 4 px one is a claim about Windows, and only
/// the guest can answer it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ContactAreaAndTwistTests
{
    private const nint Window = 0x9002;

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
        windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(0, 0, 800, 600));

        _runner = new PointerActionRunner(
            _injector, Substitute.For<IElementInspector>(), windows);
    }

    /// <summary>A stated contact area reaches the injector.</summary>
    /// <remarks>
    /// W3C gives a touch <c>pointerDown</c> a <c>width</c> and <c>height</c> in
    /// pixels — the size of the fingertip. This driver validated the pair (both
    /// present, both at least 1) and then injected a fixed box.
    /// </remarks>
    [Test]
    public void AStatedContactArea_ReachesTheInjector()
    {
        SyntheticContact pressed = Press("touch", "\"width\": 40, \"height\": 24");

        pressed.Width.ShouldBe(40);
        pressed.Height.ShouldBe(24);
    }

    /// <summary>An unstated area is a default, not a fault.</summary>
    /// <remarks>
    /// <b>The control for the area.</b> The JSON Wire <c>/touch/*</c> routes
    /// build their steps internally and name no size, so a contact that required
    /// the keys would refuse every classic touch gesture — the whole
    /// <c>TouchClick</c>, <c>TouchScroll</c> and <c>TouchFlick</c> family, which
    /// passes today.
    /// </remarks>
    [Test]
    public void AContactWithNoStatedArea_KeepsTheDefault()
    {
        SyntheticContact pressed = Press("touch", null);

        pressed.Width.ShouldBe(SyntheticContact.DefaultContactSize);
        pressed.Height.ShouldBe(SyntheticContact.DefaultContactSize);
    }

    /// <summary>A stated twist reaches the injector.</summary>
    /// <remarks>
    /// <c>twist</c> is a pen's rotation about its own axis, 0 to 359 degrees.
    /// Validated against exactly that range — including rejecting a float,
    /// because the message says "integer" — and then discarded.
    /// </remarks>
    [Test]
    public void AStatedTwist_ReachesTheInjector()
    {
        Press("pen", "\"twist\": 275").Twist.ShouldBe(275);
    }

    /// <summary>An unstated twist is zero.</summary>
    /// <remarks>THE CONTROL for twist, for the same reason as the area above.</remarks>
    [Test]
    public void AContactWithNoStatedTwist_IsUnrotated()
    {
        Press("pen", null).Twist.ShouldBe(0);
    }

    /// <summary>Pressure and tilt still arrive.</summary>
    /// <remarks>
    /// <b>The regression control.</b> These two already reached the injector, and
    /// they travel in the same constructor call as the three being added. A
    /// positional argument inserted in the wrong place would silently shift them
    /// — the kind of change that compiles, passes the new tests, and quietly
    /// swaps two doubles.
    /// </remarks>
    [Test]
    public void PressureAndTilt_StillArrive()
    {
        SyntheticContact pressed = Press(
            "pen", "\"pressure\": 0.75, \"tiltX\": -30, \"tiltY\": 45, \"twist\": 10");

        pressed.Pressure.ShouldBe(0.75);
        pressed.TiltX.ShouldBe(-30);
        pressed.TiltY.ShouldBe(45);
        pressed.Twist.ShouldBe(10);
    }

    /// <summary>The press, with whatever extra properties the caller names.</summary>
    private SyntheticContact Press(string kind, string? properties)
    {
        _injector.ClearReceivedCalls();

        string extra = properties is null ? string.Empty : $", {properties}";

        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "actions": [{
                "type": "pointer",
                "id": "device",
                "parameters": { "pointerType": "{{kind}}" },
                "actions": [
                  { "type": "pointerMove", "origin": "viewport", "x": 50, "y": 60 },
                  { "type": "pointerDown", "button": 0{{extra}} },
                  { "type": "pointerUp", "button": 0 }
                ]
              }]
            }
            """);

        _runner.Perform(document.RootElement, Window)
            .ShouldBeNull("the gesture must run for its contact to be readable");

        List<SyntheticContact> pressed = _injector.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ISyntheticPointer.Inject))
            .Select(call => (IReadOnlyList<SyntheticContact>)call.GetArguments()[0]!)
            .SelectMany(contacts => contacts)
            .Where(contact => contact.Phase == SyntheticContactPhase.Down)
            .ToList();

        pressed.Count.ShouldBe(1, "one press per gesture");
        return pressed[0];
    }
}

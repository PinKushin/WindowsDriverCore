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
/// A pointerDown says WHICH button, and the pen has three.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by reading a failure message rather than a test name.</b>
/// <c>Pen_Click_BarrelButton</c> is in the guest backlog and looked like a
/// missing flag on the injection call. Its actual failure is <i>"An element
/// could not be located on the page"</i> — because the test presses the barrel
/// button to raise a context menu and then finds "Delete" inside it. Injecting
/// an ordinary tip contact raises no menu, so the FIND is what fails, several
/// steps downstream of the real cause.
/// </para>
/// <para>
/// <c>SyntheticContact</c> carried pressure and tilt and no button at all, so
/// every <c>pointerDown</c> was a tip press whatever the payload said.
/// </para>
/// <para>
/// <b>What this fixture does and does not establish.</b> It shows the button
/// survives the protocol layer and reaches the injector. Whether
/// <c>PEN_FLAG_BARREL</c> then causes a UWP list item to open its context flyout
/// is a claim about Windows and about the application, and only the guest can
/// answer it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PenButtonTests
{
    private const nint Window = 0x9001;

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

    /// <summary>Button 2 on a pen is the barrel.</summary>
    [Test]
    public void APenPressingButtonTwo_ContactsWithTheBarrel()
    {
        Press("pen", button: 2).Button.ShouldBe(SyntheticContactButton.Barrel);
    }

    /// <summary>Button 5 on a pen is the eraser.</summary>
    /// <remarks>
    /// No suite test presses it. It is here because the mapping either
    /// distinguishes buttons or it does not, and a version that recognised only
    /// the one value under test would answer "tip" for the eraser — silently
    /// reporting that a different physical act happened.
    /// </remarks>
    [Test]
    public void APenPressingButtonFive_ContactsWithTheEraser()
    {
        Press("pen", button: 5).Button.ShouldBe(SyntheticContactButton.Eraser);
    }

    /// <summary>Button 0 is the tip, which is the overwhelming majority case.</summary>
    /// <remarks>
    /// <b>The control.</b> A mapping that returned Barrel for everything would
    /// pass the first test and break every ordinary pen gesture in the suite —
    /// <c>Pen_Click</c>, <c>Pen_Scroll_Vertical</c> and <c>Pen_DragAndDrop</c>
    /// all currently pass and all press button 0.
    /// </remarks>
    [Test]
    public void APenPressingButtonZero_ContactsWithTheTip()
    {
        Press("pen", button: 0).Button.ShouldBe(SyntheticContactButton.Tip);
    }

    /// <summary>An absent button is the tip, not a fault.</summary>
    /// <remarks>
    /// The JSON Wire <c>/touch/*</c> routes build their steps internally and name
    /// no button, so a mapping that required the key would refuse every classic
    /// touch gesture.
    /// </remarks>
    [Test]
    public void AContactWithNoStatedButton_UsesTheTip()
    {
        SyntheticContact pressed = OnlyPress(
            """
            { "type": "pointerMove", "origin": "viewport", "x": 50, "y": 60 },
            { "type": "pointerDown" },
            { "type": "pointerUp" }
            """,
            "pen");

        pressed.Button.ShouldBe(SyntheticContactButton.Tip);
    }

    /// <summary>A finger has no buttons, whatever the payload claims.</summary>
    /// <remarks>
    /// <b>The second control, and it guards a real confusion.</b> W3C reuses one
    /// <c>button</c> field across pointer kinds, and 2 means "right button" for a
    /// mouse. Mapping it to a barrel for a TOUCH contact would report that a pen
    /// feature was used by a finger — the same class of lie as answering a touch
    /// request with a mouse click, which is why this driver keeps the two
    /// injection paths separate at all.
    /// </remarks>
    [Test]
    public void ATouchContact_IsAlwaysTheTip_EvenClaimingButtonTwo()
    {
        Press("touch", button: 2).Button.ShouldBe(
            SyntheticContactButton.Tip,
            "a finger has no barrel; button 2 is a mouse spelling that does not apply");
    }

    private SyntheticContact Press(string kind, int button) =>
        OnlyPress(
            $$"""
            { "type": "pointerMove", "origin": "viewport", "x": 50, "y": 60 },
            { "type": "pointerDown", "button": {{button}} },
            { "type": "pointerUp", "button": {{button}} }
            """,
            kind);

    private SyntheticContact OnlyPress(string steps, string kind)
    {
        _injector.ClearReceivedCalls();

        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "actions": [{
                "type": "pointer",
                "id": "device",
                "parameters": { "pointerType": "{{kind}}" },
                "actions": [ {{steps}} ]
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

using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsDriverCore.Platform.Windows;

/// <inheritdoc cref="IPointerInput" />
/// <remarks>
/// <para>
/// <b>One call, three events.</b> Move, button-down and button-up go in a single
/// <c>SendInput</c> batch because Windows documents that the events in one call
/// are "not interspersed with other keyboard or mouse input events inserted
/// either by the user (with the keyboard or mouse) or by calls to keybd_event,
/// mouse_event, or other calls to SendInput". Three separate calls can be split
/// by a human moving the mouse mid-click; one cannot.
/// </para>
/// <para>
/// <b>Absolute coordinates are normalised to 0..65535 across the VIRTUAL
/// desktop</b>, not the primary monitor. Using the primary monitor's metrics is
/// the classic bug here: it works until someone has a second screen, then every
/// click lands in the wrong place by a scale factor.
/// </para>
/// </remarks>
public sealed class SendInputPointer : IPointerInput
{
    private const uint InputMouse = 0;
    private const uint MoveAbsolute = 0x0001 | 0x8000 | 0x4000;   // MOVE | ABSOLUTE | VIRTUALDESK
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <inheritdoc />
    public bool ClickAt(int x, int y)
    {
        if (!TryNormalise(x, y, out int normalisedX, out int normalisedY))
        {
            return false;
        }

        return Send(
            Mouse(normalisedX, normalisedY, MoveAbsolute),
            Mouse(0, 0, LeftDown),
            Mouse(0, 0, LeftUp));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>A move with a button DOWN walks its path; a move with none jumps.</b>
    /// A window manager samples the pointer on its own message loop, so a drag
    /// delivered as one absolute jump is not a gesture it can follow - the
    /// window never moves. Measured as <c>MouseDownMoveUp</c> failing with
    /// <i>"Expected any value except {X=290,Y=154}. Actual: {X=290,Y=154}"</i>:
    /// the window sat exactly where it started.
    /// </para>
    /// <para>
    /// Exactly the defect already fixed for touch and pen, arriving separately
    /// here because mouse input goes through <c>SendInput</c> rather than the
    /// pointer-injection API and shares none of that code.
    /// </para>
    /// <para>
    /// <b>A move with no button held is left as a single jump on purpose.</b>
    /// Nothing is tracking it, walking it would emit frames no one reads, and
    /// <c>/moveto</c> before a click is by far the common case.
    /// </para>
    /// </remarks>
    public bool MoveTo(int x, int y)
    {
        if (!_pressed || !TryGetPosition(out int fromX, out int fromY))
        {
            return TryNormalise(x, y, out int jumpX, out int jumpY)
                && Send(Mouse(jumpX, jumpY, MoveAbsolute));
        }

        foreach ((int stepX, int stepY) in PathBetween(fromX, fromY, x, y))
        {
            if (!TryNormalise(stepX, stepY, out int normalisedX, out int normalisedY) ||
                !Send(Mouse(normalisedX, normalisedY, MoveAbsolute)))
            {
                return false;
            }

            // The same separation the injected paths use, and for the same
            // measured reason: frames arriving as one burst are coalesced into a
            // single jump and the gesture is lost. Below about 2 ms per frame a
            // drag stops moving the window at all.
            Thread.Sleep(FrameSeparationMilliseconds);
        }

        return true;
    }

    /// <summary>The points a dragged pointer passes through, endpoint last.</summary>
    /// <param name="fromX">Where the pointer is.</param>
    /// <param name="fromY">Where the pointer is.</param>
    /// <param name="toX">Where it is going.</param>
    /// <param name="toY">Where it is going.</param>
    /// <returns>The path, always ending exactly on the destination.</returns>
    /// <remarks>
    /// Exposed for a test because the alternative is asserting on synthesized
    /// mouse input, which needs a desktop and moves the real cursor.
    /// </remarks>
    internal static IReadOnlyList<(int X, int Y)> PathBetween(
        int fromX, int fromY, int toX, int toY)
    {
        List<(int X, int Y)> path = new(FramesPerDrag);

        for (int frame = 1; frame <= FramesPerDrag; frame++)
        {
            path.Add((
                fromX + (int)(((long)(toX - fromX) * frame) / FramesPerDrag),
                fromY + (int)(((long)(toY - fromY) * frame) / FramesPerDrag)));
        }

        return path;
    }

    /// <summary>How many steps a dragged move is broken into.</summary>
    private const int FramesPerDrag = 10;

    /// <summary>Milliseconds between the steps of a dragged move.</summary>
    private const int FrameSeparationMilliseconds = 5;

    /// <summary>Whether a button is currently held.</summary>
    /// <remarks>
    /// <b>Instance state, and the only state this type keeps.</b> A move has to
    /// know whether it is a drag, and the caller does not tell it - the JSON
    /// Wire protocol has <c>buttondown</c>, <c>moveto</c> and <c>buttonup</c> as
    /// three separate requests with nothing linking them but this.
    /// </remarks>
    private bool _pressed;

    /// <inheritdoc />
    public bool Click(PointerButton button) =>
        Send(Mouse(0, 0, DownFlag(button)), Mouse(0, 0, UpFlag(button)));

    /// <inheritdoc />
    public bool Press(PointerButton button)
    {
        bool sent = Send(Mouse(0, 0, DownFlag(button)));
        _pressed = _pressed || sent;
        return sent;
    }

    /// <inheritdoc />
    public bool Release(PointerButton button)
    {
        bool sent = Send(Mouse(0, 0, UpFlag(button)));
        if (sent)
        {
            _pressed = false;
        }

        return sent;
    }

    /// <inheritdoc />
    public bool TryGetPosition(out int x, out int y)
    {
        bool read = Win32.GetCursorPos(out Win32.Point point);
        x = point.X;
        y = point.Y;
        return read;
    }

    /// <inheritdoc />
    public bool DoubleClick(PointerButton button) =>
        Send(
            Mouse(0, 0, DownFlag(button)),
            Mouse(0, 0, UpFlag(button)),
            Mouse(0, 0, DownFlag(button)),
            Mouse(0, 0, UpFlag(button)));

    /// <summary>Converts screen pixels to the absolute 0..65535 range.</summary>
    /// <param name="x">Screen x, in pixels.</param>
    /// <param name="y">Screen y, in pixels.</param>
    /// <param name="normalisedX">The converted x.</param>
    /// <param name="normalisedY">The converted y.</param>
    /// <returns>False when the virtual desktop has no size to scale against.</returns>
    private static bool TryNormalise(int x, int y, out int normalisedX, out int normalisedY)
    {
        int left = Win32.GetSystemMetrics(SmXVirtualScreen);
        int top = Win32.GetSystemMetrics(SmYVirtualScreen);
        int width = Win32.GetSystemMetrics(SmCxVirtualScreen);
        int height = Win32.GetSystemMetrics(SmCyVirtualScreen);

        normalisedX = 0;
        normalisedY = 0;

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // The -1 matters: the range is inclusive at both ends, and without it a
        // click on the far edge lands one pixel short.
        normalisedX = (int)(((long)(x - left) * 65535) / (width - 1));
        normalisedY = (int)(((long)(y - top) * 65535) / (height - 1));
        return true;
    }

    private static uint DownFlag(PointerButton button) => button switch
    {
        PointerButton.Right => RightDown,
        PointerButton.Middle => MiddleDown,
        _ => LeftDown,
    };

    private static uint UpFlag(PointerButton button) => button switch
    {
        PointerButton.Right => RightUp,
        PointerButton.Middle => MiddleUp,
        _ => LeftUp,
    };

    private static bool Send(params Win32.Input[] batch) =>
        Win32.SendInput((uint)batch.Length, batch, Marshal.SizeOf<Win32.Input>())
            == batch.Length;

    private static Win32.Input Mouse(int x, int y, uint flags) => new()
    {
        Type = InputMouse,
        Union = new Win32.InputUnion
        {
            Mouse = new Win32.MouseInput
            {
                X = x,
                Y = y,
                MouseData = 0,
                Flags = flags,
                Time = 0,
                ExtraInfo = 0,
            },
        },
    };
}

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
    public bool MoveTo(int x, int y) =>
        TryNormalise(x, y, out int normalisedX, out int normalisedY)
        && Send(Mouse(normalisedX, normalisedY, MoveAbsolute));

    /// <inheritdoc />
    public bool Click(PointerButton button) =>
        Send(Mouse(0, 0, DownFlag(button)), Mouse(0, 0, UpFlag(button)));

    /// <inheritdoc />
    public bool Press(PointerButton button) => Send(Mouse(0, 0, DownFlag(button)));

    /// <inheritdoc />
    public bool Release(PointerButton button) => Send(Mouse(0, 0, UpFlag(button)));

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

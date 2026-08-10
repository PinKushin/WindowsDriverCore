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

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <inheritdoc />
    public bool ClickAt(int x, int y)
    {
        int left = Win32.GetSystemMetrics(SmXVirtualScreen);
        int top = Win32.GetSystemMetrics(SmYVirtualScreen);
        int width = Win32.GetSystemMetrics(SmCxVirtualScreen);
        int height = Win32.GetSystemMetrics(SmCyVirtualScreen);

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // The -1 matters: the range is inclusive at both ends, and without it a
        // click on the far edge lands one pixel short.
        int normalisedX = (int)(((long)(x - left) * 65535) / (width - 1));
        int normalisedY = (int)(((long)(y - top) * 65535) / (height - 1));

        Win32.Input[] batch =
        [
            Mouse(normalisedX, normalisedY, MoveAbsolute),
            Mouse(0, 0, LeftDown),
            Mouse(0, 0, LeftUp),
        ];

        uint sent = Win32.SendInput(
            (uint)batch.Length, batch, Marshal.SizeOf<Win32.Input>());

        return sent == batch.Length;
    }

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

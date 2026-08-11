using System;
using System.Runtime.InteropServices;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// A real top-level window of a chosen class name, created for a test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a synthesised window rather than a real one.</b> The condition under
/// test is an <c>ApplicationFrameWindow</c> that has no
/// <c>Windows.UI.Core.CoreWindow</c> child — a frame whose application is not
/// there. It cannot be produced on demand from a real packaged application:
/// measured 2026-08-11, killing the hosted process <i>destroys</i> its frame, and
/// the window that is briefly empty is empty only during a race nobody can time.
/// One such orphan was found alive on this desktop (<c>0x000502A8</c>, visible,
/// three children, no CoreWindow), which is evidence the condition occurs and no
/// help at all in reproducing it.
/// </para>
/// <para>
/// The code under test branches on exactly two observable facts: the window's
/// class name, and whether it has a CoreWindow child. A window registered under
/// that class name with no children supplies both faithfully. Nothing else about
/// a frame is consulted, so nothing else needs to be imitated.
/// </para>
/// <para>
/// No message pump. <c>EnumWindows</c> and <c>GetClassName</c> read window-manager
/// state, not messages, and the window lives for the length of one test.
/// </para>
/// </remarks>
internal sealed class RawWindow : IDisposable
{
    private const int WsOverlapped = 0x00CF0000;
    private const int SwShowNa = 8;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private readonly WindowProcedure _procedure;
    private readonly ushort _class;

    private nint _handle;

    private RawWindow(string className, string title)
    {
        // Held in a field: the delegate is passed to unmanaged code, and a local
        // would be collected while the window manager still holds the pointer.
        _procedure = DefWindowProc;

        WindowClass registration = new()
        {
            // Not optional and not defaulted: RegisterClassEx validates it and
            // fails with ERROR_INVALID_PARAMETER (87) when it is zero.
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = GetModuleHandle(null),
            ClassName = className,
        };

        _class = RegisterClassEx(ref registration);
        if (_class == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassEx failed for '{className}': {Marshal.GetLastWin32Error()}");
        }

        _handle = CreateWindowEx(
            0, className, title, WsOverlapped, 100, 100, 320, 240, 0, 0, registration.Instance, 0);

        if (_handle == 0)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed for '{className}': {Marshal.GetLastWin32Error()}");
        }

        // Shown without activating, so the test does not steal focus from
        // whatever else is running on the machine. Visible is what matters: the
        // window search skips anything invisible.
        ShowWindow(_handle, SwShowNa);
    }

    /// <summary>The window handle.</summary>
    internal nint Handle => _handle;

    /// <summary>Creates a visible, unowned, childless top-level window.</summary>
    /// <param name="className">The class name to register it under.</param>
    /// <param name="title">Its title.</param>
    /// <returns>The window, which the caller disposes.</returns>
    /// <remarks>
    /// The class name is made unique per instance by the caller where it must be,
    /// because a class name can only be registered once per process.
    /// </remarks>
    internal static RawWindow Create(string className, string title) =>
        new(className, title);

    /// <summary>Moves the window to the top of the z-order, without activating.</summary>
    /// <remarks>
    /// <c>EnumWindows</c> yields in z-order, so this decides which of two
    /// otherwise-equal candidates a search sees first. Without activation, so the
    /// test does not take focus from whatever else is on the machine.
    /// </remarks>
    internal void BringToTop() =>
        SetWindowPos(_handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>Destroys the window and unregisters its class.</summary>
    public void Dispose()
    {
        if (_handle != 0)
        {
            DestroyWindow(_handle);
            _handle = 0;
        }

        if (_class != 0)
        {
            UnregisterClass(_class, GetModuleHandle(null));
        }
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ExtraClassBytes;
        public int ExtraWindowBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(ushort atom, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}

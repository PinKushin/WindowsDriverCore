using System.Runtime.InteropServices;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// P/Invoke declarations.
/// </summary>
/// <remarks>
/// Internal, and reached only through <see cref="IWindowLocator"/> and its
/// siblings. The implementation being replaced exposed a public static Win32
/// class that route handlers called directly, which is what made every route
/// untestable without a desktop.
/// </remarks>
internal static partial class Win32
{
    /// <summary>The desktop window, which covers the whole screen.</summary>
    /// <returns>The desktop window handle.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint GetDesktopWindow();

    /// <summary>Whether a handle identifies an existing window.</summary>
    /// <param name="hWnd">The handle to test.</param>
    /// <returns>True when the window exists.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    /// <summary>Whether a window is visible.</summary>
    /// <param name="hWnd">The window.</param>
    /// <returns>True when visible.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    /// <summary>The process and thread owning a window.</summary>
    /// <param name="hWnd">The window.</param>
    /// <param name="processId">Receives the owning process id.</param>
    /// <returns>The owning thread id, or 0 when the window is invalid.</returns>
    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>Enumerates top-level windows.</summary>
    /// <param name="callback">Called for each window; return false to stop.</param>
    /// <param name="parameter">Passed through to the callback.</param>
    /// <returns>True when enumeration completed.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, nint parameter);

    /// <summary>The window's owner, for distinguishing owned pop-ups.</summary>
    /// <param name="hWnd">The window.</param>
    /// <param name="command">Which relative to fetch.</param>
    /// <returns>The related window, or zero.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint hWnd, uint command);

    /// <summary>Length of a window's title.</summary>
    /// <param name="hWnd">The window.</param>
    /// <returns>The length in characters, excluding the terminator.</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(nint hWnd);

    /// <summary>Reads a window's title bar text.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowText(nint hWnd, [Out] char[] text, int maxCount);

    /// <summary>Reads a window's bounding rectangle, in screen coordinates.</summary>
    /// <remarks>
    /// Screen coordinates, not client: the protocol reports where the window is
    /// on the desktop, and GetClientRect would answer relative to the window
    /// itself and always place it at the origin.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out Rect rect);

    /// <summary>Moves and resizes a window.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint hWnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>Shows, hides or maximizes a window.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int command);

    /// <summary>Posts a message without waiting for it to be handled.</summary>
    /// <remarks>
    /// POST rather than SEND for WM_CLOSE. SendMessage blocks until the
    /// application has finished closing, and an application showing a "save
    /// changes?" prompt never finishes — the driver would hang rather than
    /// answer the client.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);

    /// <summary>Do not change the z-order.</summary>
    internal const uint SWP_NOZORDER = 0x0004;

    /// <summary>Do not activate the window.</summary>
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Keep the current size.</summary>
    internal const uint SWP_NOSIZE = 0x0001;

    /// <summary>Keep the current position.</summary>
    internal const uint SWP_NOMOVE = 0x0002;

    /// <summary>ShowWindow: maximize.</summary>
    internal const int SW_MAXIMIZE = 3;

    /// <summary>ShowWindow: restore.</summary>
    internal const int SW_RESTORE = 9;

    /// <summary>Asks a window to close.</summary>
    internal const uint WM_CLOSE = 0x0010;

    /// <summary>Brings a window to the foreground.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    /// <summary>The window currently in the foreground.</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    /// <summary>Raises a window to the top of the z-order.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint hWnd);

    /// <summary>Joins two threads' input queues.</summary>
    /// <remarks>
    /// The only supported way to give a background process the right to
    /// foreground a window: Windows grants that right to whoever owns the
    /// foreground input queue, so a driver has to join it first and detach
    /// afterwards.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint attaching, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    /// <summary>The calling thread's id.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    /// <summary>Screen metrics, including the virtual desktop's extent.</summary>
    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    /// <summary>Injects synthetic input.</summary>
    /// <remarks>
    /// DllImport rather than LibraryImport: the array parameter of a struct
    /// containing an explicit-layout union is not something the source generator
    /// will marshal, and forcing it produces worse code than the runtime
    /// marshaller does here.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <summary>Win32 <c>INPUT</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    /// <summary>Win32 <c>INPUT</c>'s union.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    /// <summary>Win32 <c>KEYBDINPUT</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>Win32 <c>MOUSEINPUT</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>Win32 <c>RECT</c>.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Reads a window's class name.</summary>
    /// <param name="hWnd">The window.</param>
    /// <param name="className">Receives the class name.</param>
    /// <param name="maxCount">Capacity of <paramref name="className"/>.</param>
    /// <returns>The number of characters copied.</returns>
    /// <remarks>
    /// A char buffer rather than a StringBuilder: CA1838 flags StringBuilder in
    /// P/Invoke because it forces an extra native-to-managed copy on every call,
    /// and this one runs inside a window-enumeration loop.
    ///
    /// Still <c>DllImport</c> rather than <c>LibraryImport</c>, unlike everything
    /// else here. The source generator rejects <c>[Out] char[]</c> unless the
    /// whole assembly disables runtime marshalling (SYSLIB1051), which would
    /// change how every other signature in this file marshals. Not worth it for
    /// one buffer.
    /// </remarks>
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hWnd, [Out] char[] className, int maxCount);

    /// <summary>Enumerates the child windows of a window.</summary>
    /// <remarks>
    /// Needed to reach a packaged application's <c>Windows.UI.Core.CoreWindow</c>,
    /// which is the only place the hosted application's process id is visible:
    /// the <c>ApplicationFrameWindow</c> above it belongs to ApplicationFrameHost.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);

    /// <summary>Callback for <see cref="EnumWindows"/>.</summary>
    /// <param name="hWnd">The current window.</param>
    /// <param name="parameter">The value passed to EnumWindows.</param>
    /// <returns>True to continue enumerating.</returns>
    internal delegate bool EnumWindowsProc(nint hWnd, nint parameter);

    /// <summary>GetWindow: fetch the owner window.</summary>
    internal const uint GW_OWNER = 4;
}

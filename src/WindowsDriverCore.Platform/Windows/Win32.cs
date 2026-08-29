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

    /// <summary>Walks to a window's root, parent or owner.</summary>
    /// <remarks>
    /// Used with <see cref="GA_ROOT"/> to turn a UWP <c>CoreWindow</c> into the
    /// <c>ApplicationFrameWindow</c> that actually hosts it. The parent link is
    /// the exact answer for THAT window; searching for a frame by process id
    /// instead can match a different instance of the same application, which was
    /// measured doing exactly that.
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hWnd, uint flags);

    /// <summary>The window at a screen point.</summary>
    /// <param name="point">Screen coordinates.</param>
    /// <returns>The deepest visible, enabled window there, or zero.</returns>
    /// <remarks>
    /// Answers "what is actually at this point", which is a different question
    /// from "is this point inside that window's rectangle". A covered window
    /// satisfies the second and fails the first, and only the first says where a
    /// synthesized click will land.
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(Point point);


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

    /// <summary>Sends a message and waits for the target thread to process it.</summary>
    /// <param name="hWnd">The window whose thread should process it.</param>
    /// <param name="message">The message; <see cref="WM_NULL"/> to synchronise only.</param>
    /// <param name="wParam">Unused for WM_NULL.</param>
    /// <param name="lParam">Unused for WM_NULL.</param>
    /// <param name="flags">Timeout behaviour.</param>
    /// <param name="timeout">Milliseconds to wait.</param>
    /// <param name="result">The message result, unused here.</param>
    /// <returns>Zero on timeout or failure.</returns>
    /// <remarks>
    /// The synchronisation behind typing. <c>SendInput</c> only QUEUES input; the
    /// application processes it on its own message loop, and a driver that
    /// answers the client immediately lets the client read the control before the
    /// keystrokes have landed. A message queue is ordered, so once a synchronous
    /// <c>WM_NULL</c> comes back, everything queued before it has been consumed.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    internal static partial nint SendMessageTimeout(
        nint hWnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    /// <summary>Waits until a process has drained its input and is idle.</summary>
    /// <param name="process">A handle with QUERY_INFORMATION and SYNCHRONIZE.</param>
    /// <param name="milliseconds">How long to wait.</param>
    /// <returns>0 when idle, 258 (WAIT_TIMEOUT) when it never became idle.</returns>
    /// <remarks>
    /// <b>The only one of three candidates that worked.</b> A synchronous
    /// <c>WM_NULL</c> is delivered ahead of queued input and returns at once;
    /// <c>AttachThreadInput</c> plus <c>GetQueueStatus</c> reported zero pending
    /// keys while 51 were still queued. This one returned with all 52 characters
    /// present, five times out of five.
    ///
    /// The documented caveat that it "waits only once" for a process does NOT
    /// hold for this usage — measured across five bursts on one handle, waiting
    /// 46-195 ms each time.
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial uint WaitForInputIdle(nint process, uint milliseconds);

    /// <summary>Opens a process handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    /// <summary>Closes a handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>Enough to ask a process whether it is idle.</summary>
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;

    /// <summary>Required to wait on a process handle.</summary>
    internal const uint SYNCHRONIZE = 0x00100000;

    /// <summary>Which window on a thread has focus, capture and so on.</summary>
    /// <param name="threadId">The thread, or 0 for the foreground thread.</param>
    /// <param name="info">Receives the thread's GUI state.</param>
    /// <returns>False when the thread has no GUI state.</returns>
    /// <remarks>
    /// Needed to find the window keystrokes will actually reach. For a packaged
    /// application the session's window is the <c>ApplicationFrameWindow</c>,
    /// which belongs to <c>ApplicationFrameHost.exe</c> — a different process and
    /// thread from the application. Synchronising on it waits on the wrong
    /// message queue and returns immediately, which is measurably no wait at all.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    /// <summary>Win32 <c>GUITHREADINFO</c>.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rect CaretRect;
    }

    /// <summary>Do not hang if the target thread stops responding.</summary>
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>A message with no effect, sent purely to synchronise.</summary>
    internal const uint WM_NULL = 0x0000;

    /// <summary>Maps a character to a virtual-key code in the current layout.</summary>
    /// <param name="character">The character, as a UTF-16 code unit.</param>
    /// <returns>
    /// Low byte is the virtual-key code, high byte the shift state; -1 when the
    /// character has no key in the current layout.
    /// </returns>
    /// <remarks>
    /// Needed because <c>KEYEVENTF_UNICODE</c> bypasses the keyboard layout, so
    /// modifier state does not combine with it — an "a" injected as unicode
    /// while control is held arrives as the letter, not as Ctrl+A.
    ///
    /// Takes a <c>ushort</c> rather than a <c>char</c> because the source
    /// generator will not marshal <c>char</c> without disabling runtime
    /// marshalling for the whole assembly. A UTF-16 code unit is exactly what
    /// the API wants, so the cast at the call site costs nothing.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanW")]
    internal static partial short VkKeyScan(ushort character);

    /// <summary>Reads the cursor's current screen position.</summary>
    /// <remarks>
    /// Needed because the protocol's <c>/moveto</c> with offsets and no element
    /// means "from where the pointer is now". The driver keeps no cursor
    /// position of its own — the system's is the only one that cannot go stale
    /// when the user moves the mouse.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);

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

    /// <summary>ShowWindow: minimize without activating the next window.</summary>
    /// <remarks>
    /// <c>SW_MINIMIZE</c> (6) minimizes AND activates whatever is next in the
    /// Z order, which steals the foreground from a window the client never
    /// mentioned. <c>SW_SHOWMINNOACTIVE</c> minimizes and leaves activation
    /// alone, which is what a driver command should do.
    /// </remarks>
    internal const int SW_SHOWMINNOACTIVE = 7;

    /// <summary>ShowWindow: restore.</summary>
    internal const int SW_RESTORE = 9;

    /// <summary>Asks a window to close.</summary>
    /// <summary>The desktop shell's window, or 0 when there is no shell.</summary>
    /// <remarks>
    /// Used to identify the process that must never be killed. Comparing against
    /// the name "explorer.exe" would also match a File Explorer running as its
    /// own process and refuse to close something that safely could be; the shell
    /// window names the shell exactly.
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    /// <summary>Prepares the process to inject touch. Once only; a second call fails.</summary>
    /// <remarks>
    /// Windows 8 and later, so inside this driver's Windows 10 1607 floor.
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeTouchInjection(uint maxCount, uint dwMode);

    /// <summary>Injects one frame of touch contacts.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InjectTouchInput(
        uint count,
        [In] SyntheticPointer.PointerTouchInfo[] contacts);

    /// <summary>Creates a synthetic pen device.</summary>
    /// <remarks>
    /// Windows 10 <b>1809</b>, which is ABOVE the 1607 floor - so this can
    /// genuinely be absent on a supported system and the caller must cope rather
    /// than assume.
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreateSyntheticPointerDevice(
        uint pointerType, uint maxCount, uint mode);

    /// <summary>Injects one frame through a synthetic pointer device.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InjectSyntheticPointerInput(
        nint device,
        [In] SyntheticPointer.PointerTypeInfo[] pointerInfo,
        uint count);

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

    /// <summary>Win32 <c>POINT</c>.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
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

    /// <summary>GetAncestor flag: the root window of the chain.</summary>
    /// <summary>GetAncestor flag: the immediate parent, desktop for a top-level window.</summary>
    /// <remarks>
    /// GA_ROOT cannot answer "is this top level": measured in MainWindowWaiter,
    /// GA_ROOT of a UWP CoreWindow returns the CoreWindow ITSELF, so a child
    /// window looks like its own root. GA_PARENT compared against the desktop
    /// does answer it.
    /// </remarks>
    internal const uint GA_PARENT = 1;

    internal const uint GA_ROOT = 2;
}

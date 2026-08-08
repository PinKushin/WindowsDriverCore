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

    /// <summary>Callback for <see cref="EnumWindows"/>.</summary>
    /// <param name="hWnd">The current window.</param>
    /// <param name="parameter">The value passed to EnumWindows.</param>
    /// <returns>True to continue enumerating.</returns>
    internal delegate bool EnumWindowsProc(nint hWnd, nint parameter);

    /// <summary>GetWindow: fetch the owner window.</summary>
    internal const uint GW_OWNER = 4;
}

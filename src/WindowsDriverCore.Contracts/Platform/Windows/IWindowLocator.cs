namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Answers questions about top-level windows.
/// </summary>
/// <remarks>
/// An interface for the same reason as the launcher: the protocol layer must be
/// testable without a desktop. The implementation being replaced called static
/// Win32 P/Invoke directly from route handlers, so no route could be tested at
/// all.
/// </remarks>
public interface IWindowLocator
{
    /// <summary>The desktop window, used by a <c>Root</c> session.</summary>
    nint DesktopWindow { get; }

    /// <summary>Whether a handle refers to a window that currently exists.</summary>
    /// <param name="handle">The window handle.</param>
    /// <returns>True when the window exists.</returns>
    bool Exists(nint handle);

    /// <summary>The process that owns a window.</summary>
    /// <param name="handle">The window handle.</param>
    /// <returns>The owning process id, or 0 when the window does not exist.</returns>
    int GetOwningProcessId(nint handle);

    /// <summary>The window's title bar text.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>The title, or an empty string if the window has none or is gone.</returns>
    /// <remarks>
    /// Empty rather than null for a window with no title: a titleless window is
    /// a normal thing, not an error, and the protocol has no way to say "absent"
    /// for a title anyway.
    /// </remarks>
    string GetTitle(nint handle);

    /// <summary>The window's position and size on screen.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>The bounds, or null if the window no longer exists.</returns>
    /// <remarks>
    /// Null when the window is gone, so the caller can answer "the window has
    /// been closed" rather than reporting a zero rectangle as if it were real.
    /// </remarks>
    WindowBounds? GetBounds(nint handle);

    /// <summary>Moves and resizes a window.</summary>
    /// <param name="handle">The window.</param>
    /// <param name="bounds">Where it should be and how big.</param>
    /// <returns>True if the window was still there to move.</returns>
    bool SetBounds(nint handle, WindowBounds bounds);

    /// <summary>Maximizes a window.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>True if the window was still there to maximize.</returns>
    bool Maximize(nint handle);

    /// <summary>Asks a window to close.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>True if the request was delivered.</returns>
    /// <remarks>
    /// Asks rather than forces: the window may prompt, and whether it actually
    /// closes is the application's decision. The caller cannot treat a true
    /// return as "the window is gone".
    /// </remarks>
    bool Close(nint handle);

    /// <summary>Brings a window to the foreground.</summary>
    /// <param name="handle">The window.</param>
    /// <returns>True if the window is now in the foreground.</returns>
    /// <remarks>
    /// <b>Needed before focusing or typing, and not optional.</b> UI Automation's
    /// SetFocus fails with E_INVALIDARG against a control in a background
    /// window even when it reports focusable, enabled and on screen — measured
    /// 2026-08-10. Keystrokes go wherever focus is, so without this a driver
    /// types into whatever the user last clicked.
    /// </remarks>
    bool BringToForeground(nint handle);
}

/// <summary>A window's position and size, in screen pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public sealed record WindowBounds(int X, int Y, int Width, int Height);

/// <summary>Sends real mouse input to the desktop.</summary>
/// <remarks>
/// <para>
/// <b>The last rung of the click ladder, and the one that must be guarded.</b>
/// A coordinate click that lands outside the target window is input delivered to
/// somebody else's application — on a developer's machine that opens whatever is
/// underneath, and on CI it silently accomplishes nothing. The guard lives above
/// this interface: this type dispatches, it does not decide.
/// </para>
/// <para>
/// One call carries move, button-down and button-up together. Windows documents
/// that events in a single <c>SendInput</c> call are not interspersed with input
/// from the user's own hand, so three separate calls can be split by a human
/// moving the mouse and one call cannot.
/// </para>
/// </remarks>
public interface IPointerInput
{
    /// <summary>Clicks once at a screen coordinate.</summary>
    /// <param name="x">Screen x, in pixels.</param>
    /// <param name="y">Screen y, in pixels.</param>
    /// <returns>True if the input was accepted by the system.</returns>
    bool ClickAt(int x, int y);
}

/// <summary>Sends real keyboard input to whatever has focus.</summary>
/// <remarks>
/// <b>Modifiers toggle, they do not press.</b> The JSON Wire Protocol sends a
/// modifier as a character in the stream, and it flips that key's held state:
/// <c>Control + "a" + Control</c> means hold control, press a, release control.
/// Treating each occurrence as a discrete press would send two control taps and
/// a bare "a".
/// </remarks>
public interface IKeyboardInput
{
    /// <summary>Types a WebDriver key sequence.</summary>
    /// <param name="keys">
    /// Text, which may contain WebDriver's private-use key codes such as
    /// U+E009 for control.
    /// </param>
    /// <returns>True if every keystroke was accepted by the system.</returns>
    bool Type(string keys);
}

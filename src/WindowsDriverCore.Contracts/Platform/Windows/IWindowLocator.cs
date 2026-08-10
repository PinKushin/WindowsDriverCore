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
}

/// <summary>A window's position and size, in screen pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public sealed record WindowBounds(int X, int Y, int Width, int Height);

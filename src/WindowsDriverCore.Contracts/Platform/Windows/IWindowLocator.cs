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

    /// <summary>Blocks until the window's thread has processed queued input.</summary>
    /// <param name="handle">The window whose thread to wait on.</param>
    /// <returns>False when the window is gone or its thread is not responding.</returns>
    /// <remarks>
    /// <b>Typing is asynchronous and the protocol is not.</b> <c>SendInput</c>
    /// only queues keystrokes; the application consumes them on its own message
    /// loop. Measured 2026-08-11: a 52-character string answered the client
    /// immediately, the client read the edit box and saw <c>abc</c>, and the
    /// remaining 49 characters arrived during the NEXT test — whose assertion
    /// then failed on text it never typed.
    /// </remarks>
    bool WaitForInputProcessed(nint handle);

    /// <summary>The process's current top-level window, or zero.</summary>
    /// <param name="processId">The process that owns the application.</param>
    /// <returns>A window handle, or zero when the process has none right now.</returns>
    /// <remarks>
    /// <para>
    /// <b>A session's window is not fixed for the session's life.</b> Measured
    /// 2026-08-10: a packaged application's <c>Windows.UI.Core.CoreWindow</c> is
    /// top-level and its own root at launch, and is later DESTROYED — not
    /// reparented — when the application is rehosted into its
    /// <c>ApplicationFrameWindow</c>. A session holding the original handle then
    /// answers "Currently selected window has been closed" to everything, which
    /// is what killed every <c>ActionsError_*</c> test at <c>TestInit</c>.
    /// </para>
    /// <para>
    /// Fixing it at attach time is impossible, because at that instant the frame
    /// does not exist yet — three attempts to prefer or wait for it all timed out
    /// and handed back window 0. So the session re-resolves instead, and this is
    /// how it asks.
    /// </para>
    /// </remarks>
    nint FindMainWindow(int processId);
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

    /// <summary>Moves the cursor to a screen coordinate.</summary>
    /// <param name="x">Screen x, in pixels.</param>
    /// <param name="y">Screen y, in pixels.</param>
    /// <returns>True if the input was accepted by the system.</returns>
    /// <remarks>
    /// The JSON Wire Protocol's <c>/moveto</c> and <c>/click</c> are separate
    /// commands, and the position that <c>/click</c> acts on is simply wherever
    /// the cursor now is. So this really moves the pointer rather than recording
    /// a coordinate for later — no session state, and the same thing a hand
    /// would do.
    /// </remarks>
    bool MoveTo(int x, int y);

    /// <summary>Presses and releases a button at the current position.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True if the input was accepted by the system.</returns>
    bool Click(PointerButton button);

    /// <summary>Presses a button and holds it, at the current position.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True if the input was accepted by the system.</returns>
    bool Press(PointerButton button);

    /// <summary>Releases a held button, at the current position.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True if the input was accepted by the system.</returns>
    bool Release(PointerButton button);

    /// <summary>Reads where the pointer is now.</summary>
    /// <param name="x">Receives screen x, in pixels.</param>
    /// <param name="y">Receives screen y, in pixels.</param>
    /// <returns>True if the position could be read.</returns>
    /// <remarks>
    /// On the contract rather than reached for directly, because
    /// <c>/moveto</c> with offsets and no element means "from where the pointer
    /// is now", and the protocol layer must not know how a pointer is read.
    /// </remarks>
    bool TryGetPosition(out int x, out int y);

    /// <summary>Clicks twice at the current position.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True if the input was accepted by the system.</returns>
    /// <remarks>
    /// Both clicks go in one <c>SendInput</c> batch. Two separate calls would let
    /// the user's own mouse movement land between them, which turns a double
    /// click into two single clicks — and the whole reason this exists is that
    /// the application must see the pair.
    /// </remarks>
    bool DoubleClick(PointerButton button);
}

/// <summary>Which pointer button an action applies to.</summary>
/// <remarks>
/// The values are the JSON Wire Protocol's, sent as the <c>button</c> field of
/// <c>/click</c>, <c>/buttondown</c> and <c>/buttonup</c>. They are NOT the
/// order a person would guess — middle is 1 and right is 2 — so they are named
/// here rather than passed around as bare integers.
/// </remarks>
public enum PointerButton
{
    /// <summary>The primary button. The protocol's default when none is sent.</summary>
    Left = 0,

    /// <summary>The middle button, or wheel press.</summary>
    Middle = 1,

    /// <summary>The secondary button — what opens a context menu.</summary>
    Right = 2,
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

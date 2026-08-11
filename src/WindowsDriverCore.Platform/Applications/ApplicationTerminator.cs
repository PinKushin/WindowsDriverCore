using System.ComponentModel;
using System.Diagnostics;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Platform.Applications;

/// <inheritdoc cref="IApplicationTerminator" />
/// <remarks>
/// <para>
/// Asks the application to close before killing it. A packaged application given
/// <c>CloseMainWindow</c> shuts down the way it would if a user clicked the X,
/// which lets it flush whatever it flushes; killing it outright is a last resort
/// and can leave state behind that the next launch has to recover from.
/// </para>
/// <para>
/// <b>Whether termination is ALLOWED is not decided here.</b> This ends whatever
/// process it is handed. The session knows whether this driver started the
/// application, and only it can tell the difference between an application under
/// test and a window the caller attached to.
/// </para>
/// </remarks>
public sealed class ApplicationTerminator : IApplicationTerminator
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>The process that owns the desktop shell, or 0 if there is none.</summary>
    /// <remarks>
    /// <c>GetShellWindow</c> names the desktop window exactly, so this is an
    /// identity check rather than a comparison against the string
    /// "explorer.exe" - which would also match a File Explorer started as a
    /// separate process, and would refuse to close something it safely could.
    /// </remarks>
    /// <summary>Whether a process is the desktop shell, and so must never be ended.</summary>
    /// <remarks>
    /// <b>Internal so it can be tested without being executed.</b> The obvious
    /// test - end the shell and check the desktop survived - destroys the
    /// machine it runs on when it fails, which makes it unusable as a test.
    /// Separating the DECISION from the ACTION makes the dangerous half
    /// assertable at no risk.
    /// </remarks>
    internal static bool IsTheDesktopShell(int processId) =>
        processId > 0 && processId == ShellProcessId();

    private static int ShellProcessId()
    {
        nint shell = Win32.GetShellWindow();
        if (shell == 0)
        {
            return 0;
        }

        _ = Win32.GetWindowThreadProcessId(shell, out uint pid);
        return (int)pid;
    }

    /// <summary>
    /// Whether a process still shows a visible top-level window other than one.
    /// </summary>
    /// <param name="processId">The process to look at.</param>
    /// <param name="excluding">The window that has just been closed.</param>
    /// <returns><see langword="true"/> when another visible window remains.</returns>
    /// <remarks>
    /// <para>
    /// <b>Internal and separate from the action, for the same reason
    /// <see cref="IsTheDesktopShell"/> is.</b> The decision is what needs
    /// asserting, and the obvious end-to-end test destroys whatever it was wrong
    /// about.
    /// </para>
    /// <para>
    /// <b>Visible only.</b> A process commonly owns hidden top-level windows —
    /// message-only windows, IME and tooltip helpers — and counting those would
    /// report every application as still in use, which turns termination off
    /// entirely.
    /// </para>
    /// </remarks>
    internal static bool HasAnotherVisibleWindow(int processId, nint excluding)
    {
        if (processId <= 0)
        {
            return false;
        }

        bool found = false;

        Win32.EnumWindows(
            (window, _) =>
            {
                if (window == excluding || !Win32.IsWindowVisible(window))
                {
                    return true;
                }

                if (Win32.GetWindowThreadProcessId(window, out uint owner) == 0 ||
                    (int)owner != processId)
                {
                    return true;
                }

                found = true;

                // Stop: one is the whole answer, and enumerating every window on
                // the desktop to count them would be work for a number nobody
                // reads.
                return false;
            },
            0);

        return found;
    }

    /// <summary>Waits for a window to disappear, briefly.</summary>
    private static bool WaitUntilGone(nint window)
    {
        // Spins on the observation rather than sleeping a guessed interval: the
        // window either goes or it does not, and a fixed sleep would be a bet on
        // how fast the application closes.
        SpinWait.SpinUntil(() => !Win32.IsWindow(window), GracePeriod);

        return !Win32.IsWindow(window);
    }

    /// <inheritdoc />
    public bool Terminate(int processId, nint window)
    {
        // THE WINDOW FIRST, AND FOR THE SHELL IT IS THE ONLY THING TOUCHED.
        //
        // A File Explorer window belongs to the long-running explorer.exe that
        // draws the desktop, taskbar and Start menu. The launcher tracks the
        // window's OWNING process, so a session on Explorer tracks the shell -
        // and the code below would call CloseMainWindow on it and then Kill it,
        // taking the desktop down with the session.
        //
        // Measured 2026-08-11: the suite opens Explorer windows every run and
        // six or seven survived, because the only safe thing this could do was
        // nothing. Closing the window is both safe and correct.
        bool isTheShell = IsTheDesktopShell(processId);

        if (window != 0 && Win32.IsWindow(window))
        {
            Win32.PostMessage(window, Win32.WM_CLOSE, 0, 0);

            // GONE IS OFTEN THE WHOLE JOB, and the shell was only the first case
            // of it.
            //
            // A process that still shows another visible window is being used by
            // somebody. On Windows 11 that is the ordinary case rather than an
            // exotic one: Notepad, and Store applications generally, are
            // single-instance and multi-window, so a second window belongs to the
            // person at the desk and not to this session. Killing the process
            // takes their window with it — and the packaged host then treats the
            // death as a crash and restores the session it just lost.
            //
            // Found from the test side first: a cleanup that reasoned about
            // PROCESSES could not see what a launch adds to a single-instance
            // application, because the process count does not move. The driver
            // had the same blind spot, guarded only for explorer.exe — where the
            // consequence was loud enough to have already been noticed.
            //
            // WinAppDriver terminates by process id, so it closes the user's
            // other windows on Windows 11. That is a defect rather than a
            // contract, which this project fixes rather than reproduces.
            if (WaitUntilGone(window) &&
                (isTheShell || HasAnotherVisibleWindow(processId, window)))
            {
                return true;
            }
        }

        if (isTheShell)
        {
            // Never, under any circumstances. A session that cannot close its
            // window is a failed teardown; a dead shell is a dead desktop, and
            // every later test in the run measures nothing.
            return false;
        }

        if (processId <= 0)
        {
            return true;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);

            if (process.HasExited)
            {
                return true;
            }

            // CloseMainWindow only works for a process with a message loop, and
            // returns false rather than throwing when there is none — so its
            // result is checked rather than assumed, and the kill below is the
            // path for a windowless one.
            if (process.CloseMainWindow() && process.WaitForExit(GracePeriod))
            {
                return true;
            }

            // NOT entireProcessTree. A packaged application's window is hosted
            // by ApplicationFrameHost, which hosts every OTHER packaged
            // application's window too — taking the tree down can close
            // applications that have nothing to do with this session. Measured
            // 2026-08-10: WinAppDriver closes the application on DELETE
            // /session (calculators 1 then 0), so the behaviour is right; the
            // blast radius was not.
            process.Kill();
            return process.WaitForExit(GracePeriod);
        }
        catch (ArgumentException)
        {
            // GetProcessById throws this when the id names nothing, which means
            // the application is already gone — the caller's goal, reached
            // without help.
            return true;
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and the call.
            return true;
        }
        catch (Win32Exception)
        {
            // Access denied: an elevated application cannot be closed by a
            // driver running unelevated. Reported rather than swallowed, so the
            // caller can say the session ended but the application did not.
            return false;
        }
    }
}

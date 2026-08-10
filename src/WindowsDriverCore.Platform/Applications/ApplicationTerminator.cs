using System.ComponentModel;
using System.Diagnostics;

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

    /// <inheritdoc />
    public bool Terminate(int processId)
    {
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

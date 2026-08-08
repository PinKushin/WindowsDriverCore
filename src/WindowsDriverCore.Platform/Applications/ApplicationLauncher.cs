using System.Collections.Generic;
using System.Diagnostics;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Platform.Applications;

/// <summary>
/// Starts applications and waits for the window to drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Classic applications only, for now.</b> A packaged application is
/// identified by an AUMID containing <c>!</c> — for example
/// <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c> — and cannot be started
/// with <c>Process.Start</c>. It needs <c>IApplicationActivationManager</c>,
/// which is the next piece of work. Until then an AUMID fails with the
/// file-not-found message, which is wrong but visible rather than silent.
/// </para>
/// <para>
/// Nothing here is covered by the protocol tests: they substitute
/// <see cref="IApplicationLauncher"/> precisely so session creation can be
/// tested without a desktop. This type needs integration tests against a real
/// application, and does not have them yet.
/// </para>
/// </remarks>
public sealed class ApplicationLauncher : IApplicationLauncher
{
    private const string FileNotFoundMessage = "The system cannot find the file specified";
    private const string NoWindowMessage = "Could not find main window for application";
    private const string InvalidDirectoryMessage = "The directory name is invalid";

    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly MainWindowWaiter _waiter;
    private readonly IWindowLocator _windows;

    /// <summary>Creates the launcher.</summary>
    /// <param name="waiter">Waits for the application window.</param>
    /// <param name="windows">Resolves window ownership.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ApplicationLauncher(MainWindowWaiter waiter, IWindowLocator windows)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        ArgumentNullException.ThrowIfNull(windows);

        _waiter = waiter;
        _windows = windows;
    }

    /// <inheritdoc />
    public LaunchResult Launch(ApplicationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Blocking on the async wait. The route handler is async, so this could
        // flow through — but IApplicationLauncher is deliberately synchronous
        // because launching is a single logical step from the protocol's point
        // of view, and an async seam here would spread through every caller for
        // no behavioural gain.
        return LaunchCore(target).GetAwaiter().GetResult();
    }

    private async Task<LaunchResult> LaunchCore(ApplicationTarget target)
    {
        if (!string.IsNullOrEmpty(target.WorkingDirectory) &&
            !Directory.Exists(target.WorkingDirectory))
        {
            // Checked before starting anything: a bad working directory should
            // not leave a process running.
            return LaunchResult.Failure(InvalidDirectoryMessage);
        }

        // Snapshot BEFORE launching. A packaged application's window belongs to
        // ApplicationFrameHost rather than to the process activation returns, so
        // the only way to recognise it is that the frame window is new.
        IReadOnlySet<nint> framesBefore = MainWindowWaiter.SnapshotFrameWindows();

        int processId;
        try
        {
            processId = StartProcess(target);
        }
        catch (FileNotFoundException)
        {
            return LaunchResult.Failure(FileNotFoundMessage);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Process.Start reports a missing executable as Win32Exception rather
            // than FileNotFoundException. Both mean the same thing to a client.
            return LaunchResult.Failure(FileNotFoundMessage);
        }

        nint window = await _waiter
            .WaitAsync(processId, framesBefore, WindowTimeout, PollInterval)
            .ConfigureAwait(false);

        if (window == 0)
        {
            return LaunchResult.Failure(NoWindowMessage);
        }

        // The window's owner is the process to track, which for a packaged app is
        // not the one activation returned. Getting this wrong makes every later
        // process-scoped operation address the broker instead of the app.
        int owningProcess = _windows.GetOwningProcessId(window);

        return LaunchResult.Success(
            new LaunchedApplication(owningProcess != 0 ? owningProcess : processId, window));
    }

    private static int StartProcess(ApplicationTarget target)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = target.App,
            UseShellExecute = false,
        };

        if (!string.IsNullOrEmpty(target.Arguments))
        {
            // appArguments is defined by the protocol as a command-line string,
            // so it is passed as one. With UseShellExecute false there is no
            // shell to inject into — this is argument passing, not command
            // injection. The vulnerability in the implementation being replaced
            // was different in kind: it interpolated a capability into a
            // PowerShell -Command string, where quoting really can be escaped.
            startInfo.Arguments = target.Arguments;
        }

        if (!string.IsNullOrEmpty(target.WorkingDirectory))
        {
            startInfo.WorkingDirectory = target.WorkingDirectory;
        }

        using Process? process = Process.Start(startInfo);
        return process?.Id ?? 0;
    }
}

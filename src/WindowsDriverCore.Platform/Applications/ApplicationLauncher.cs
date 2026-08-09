using System.Collections.Generic;
using System.Diagnostics;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Platform.Applications;

/// <summary>
/// Starts applications and waits for the window to drive.
/// </summary>
/// <remarks>
/// Two launch paths. A packaged application is identified by an AUMID
/// containing <c>!</c> — <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c> —
/// and goes through <c>IApplicationActivationManager</c>, because
/// <c>Process.Start</c> cannot start one. Anything else is a classic
/// executable and goes through <c>Process.Start</c>.
///
/// Nothing here is covered by the protocol tests, which substitute
/// <see cref="IApplicationLauncher"/> so session creation can be tested without
/// a desktop. It is covered by the integration suite, which drives real
/// applications.
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

        // Snapshot BEFORE launching. Several applications never give the launched
        // process a window: a packaged app's belongs to ApplicationFrameHost, and
        // Windows 11's notepad.exe is a stub that starts the real app and exits.
        // Recognising the window as one that did not exist a moment ago is the
        // only thing that covers both.
        IReadOnlySet<nint> windowsBefore = MainWindowWaiter.SnapshotTopLevelWindows();

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

        if (processId == 0)
        {
            // Activation rejected the AUMID: the package is not installed, or it
            // does not declare that application id. From the client's side the
            // app could not be found, so it gets the same message.
            return LaunchResult.Failure(FileNotFoundMessage);
        }

        nint window = await _waiter
            .WaitAsync(processId, windowsBefore, WindowTimeout, PollInterval)
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

    /// <summary>
    /// Whether an app value names a packaged application rather than an
    /// executable.
    /// </summary>
    /// <remarks>
    /// An AUMID is <c>PackageFamilyName!ApplicationId</c>. The rooted-path check
    /// matters because a file path may legitimately contain <c>!</c>, and
    /// treating <c>C:\tools\build!final\app.exe</c> as an AUMID would send it to
    /// COM activation and fail for a reason that names the wrong thing.
    /// </remarks>
    internal static bool IsPackagedApplication(string app) =>
        app.Contains('!', StringComparison.Ordinal) && !Path.IsPathRooted(app);

    private static int StartProcess(ApplicationTarget target) =>
        IsPackagedApplication(target.App)
            ? ActivatePackagedApplication(target)
            : StartClassicProcess(target);

    private static int ActivatePackagedApplication(ApplicationTarget target)
    {
        // Through object deliberately: the coclass does not declare the
        // interface, so a direct cast will not compile. Going via object makes
        // the runtime issue a QueryInterface, which is what actually binds them.
        object activator = new ApplicationActivationManager();
        IApplicationActivationManager manager = (IApplicationActivationManager)activator;

        int hr = manager.ActivateApplication(
            target.App,
            target.Arguments,
            ActivateOptions.None,
            out uint processId);

        // The HRESULT is returned rather than thrown. Marshal.ThrowExceptionForHR
        // maps each code to a different exception type, so catching COMException
        // is not enough — an unregistered package returns E_INVALIDARG and
        // arrives as ArgumentException, which sailed straight past the handler
        // and out of the launcher. Reading the code directly removes the guessing.
        //
        // Incidentally this is where the previous implementation's mysterious
        // "Value does not fall within the expected range" came from: that is
        // E_INVALIDARG's stock message, surfaced without ever being identified.
        return hr < 0 ? 0 : (int)processId;
    }

    private static int StartClassicProcess(ApplicationTarget target)
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

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

        // Which processes existed a moment ago, so an activation that ATTACHED
        // to a running application can be told from one that started it.
        // Windows 10's Calculator is single-instance and returns the existing
        // process, and treating that as a launch means the session claims the
        // right to end an application it did not start.
        HashSet<int> processesBefore = [];
        foreach (Process existing in Process.GetProcesses())
        {
            processesBefore.Add(existing.Id);
            existing.Dispose();
        }

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
            // ACTIVATION'S OWN REASON, NOT A SUBSTITUTE FOR IT. The suite
            // asserts these character for character:
            // CreateSessionError_InvalidAppIdModernApp sends
            // "Microsoft.BadAppId!App" and expects
            // "Value does not fall within the expected range." - E_INVALIDARG's
            // stock wording, surfaced through WinAppDriver rather than composed
            // by it.
            //
            // Marshal.GetExceptionForHR is what produces those sentences, so
            // asking it is what reproduces every one of them; hard-coding the
            // one the suite happens to check would answer that input correctly
            // and every other activation failure wrongly.
            return LaunchResult.Failure(ActivationFailureMessage());
        }

        nint window = await _waiter
            .WaitAsync(processId, windowsBefore, WindowTimeout, PollInterval)
            .ConfigureAwait(false);

        if (window == 0)
        {
            return LaunchResult.Failure(NoWindowMessage);
        }

        // The process to track is the one whose CONTENT the window shows, which
        // for a packaged app is neither the process activation returned nor the
        // window's owner. Getting this wrong makes every later process-scoped
        // operation address the broker instead of the app — and DELETE /session
        // terminates what it tracks, so aimed at ApplicationFrameHost it would
        // close every UWP window on the machine.
        int owningProcess = _windows.GetHostedProcessId(window);

        return LaunchResult.Success(
            new LaunchedApplication(
                owningProcess != 0 ? owningProcess : processId,
                window,
                Started: !processesBefore.Contains(owningProcess != 0 ? owningProcess : processId)));
    }

    /// <summary>The message belonging to the last activation HRESULT.</summary>
    /// <remarks>
    /// Falls back to the file-not-found wording when there is no HRESULT to
    /// report - a classic process that produced no id, which has no COM error
    /// behind it.
    /// </remarks>
    private string ActivationFailureMessage()
    {
        if (_lastActivationHResult >= 0)
        {
            return FileNotFoundMessage;
        }

        return System.Runtime.InteropServices.Marshal
            .GetExceptionForHR(_lastActivationHResult)?.Message ?? FileNotFoundMessage;
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

    private int StartProcess(ApplicationTarget target) =>
        IsPackagedApplication(target.App)
            ? ActivatePackagedApplication(target)
            : StartClassicProcess(target);

    /// <summary>
    /// The HRESULT of the last packaged activation, or 0 when none has failed.
    /// </summary>
    /// <remarks>
    /// <b>Because the code is the message.</b> The compatibility suite asserts
    /// on activation failures character for character -
    /// CreateSessionError_InvalidAppIdModernApp expects
    /// "Value does not fall within the expected range.", which is E_INVALIDARG's
    /// stock wording surfacing through WinAppDriver rather than a sentence
    /// WinAppDriver wrote. Flattening every activation failure to "the system
    /// cannot find the file specified" throws away the one thing the caller
    /// needs.
    ///
    /// Instance state rather than a return value only because StartProcess is
    /// shared with the classic path, which has no HRESULT to report.
    /// </remarks>
    private int _lastActivationHResult;

    private int ActivatePackagedApplication(ApplicationTarget target)
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
        _lastActivationHResult = hr;
        return hr < 0 ? 0 : (int)processId;
    }

    /// <summary>
    /// Resolves a <c>System32</c> path the way a 32-bit client means it.
    /// </summary>
    /// <param name="app">The executable path a client sent.</param>
    /// <returns>The path to start, redirected only when that is what resolves it.</returns>
    /// <remarks>
    /// <para>
    /// <b>MEASURED 2026-08-11</b>, on the Windows 11 host and the Windows 10 guest
    /// alike:
    /// </para>
    /// <code>
    /// C:\Windows\System32\explorer.exe   absent
    /// C:\Windows\SysWOW64\explorer.exe   present
    /// C:\Windows\explorer.exe             present
    /// </code>
    /// <para>
    /// <b>WinAppDriver.exe is a 32-bit process</b> — measured from its PE header,
    /// machine <c>0x014C</c>, no CLR directory. WOW64 therefore redirects every
    /// <c>System32</c> path it opens to <c>SysWOW64</c>, silently and by design.
    /// So when a client says <c>C:\Windows\System32\explorer.exe</c> to
    /// WinAppDriver it gets the SysWOW64 file, and the compatibility suite
    /// hardcodes exactly that path in <c>CommonTestSettings.ExplorerAppId</c>.
    /// </para>
    /// <para>
    /// This driver is 64-bit, sees the real <c>System32</c>, finds nothing, and
    /// answers "The system cannot find the file specified" — which is literally
    /// true and useless. Nine suite tests fail on it. The incompatibility is one
    /// of process architecture, not of behaviour, and it cannot be fixed by
    /// matching a message.
    /// </para>
    /// <para>
    /// <b>Only when the original does not exist</b>, so a real 64-bit
    /// <c>System32</c> executable is never silently swapped for its 32-bit
    /// sibling. That ordering is the whole safety of this: it can rescue a path
    /// that would otherwise fail and can never redirect one that works.
    /// </para>
    /// </remarks>
    private static string AsA32BitClientMeansIt(string app)
    {
        if (string.IsNullOrEmpty(app) || File.Exists(app))
        {
            return app;
        }

        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!app.StartsWith(system32 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return app;
        }

        string wow64 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");

        string redirected = Path.Combine(wow64, app[(system32.Length + 1)..]);

        return File.Exists(redirected) ? redirected : app;
    }

    private static int StartClassicProcess(ApplicationTarget target)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = AsA32BitClientMeansIt(target.App),
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

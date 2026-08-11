namespace WindowsDriverCore.Platform.Applications;

/// <summary>What to launch.</summary>
/// <param name="App">An application id or a full executable path.</param>
/// <param name="Arguments">Launch arguments, or null.</param>
/// <param name="WorkingDirectory">Working directory, or null. Classic apps only.</param>
public sealed record ApplicationTarget(string App, string? Arguments, string? WorkingDirectory);

/// <summary>An application that is running and has a window.</summary>
/// <param name="ProcessId">
/// The process that owns the window. For a packaged app this is the process the
/// window actually belongs to, not the broker that activation returns.
/// </param>
/// <param name="WindowHandle">The top-level window to drive.</param>
/// <param name="Started">
/// Whether this launch STARTED the process, as opposed to attaching to one that
/// was already running.
/// <para>
/// <b>Not the same question as "did activation succeed".</b> Windows 10's
/// Calculator is single-instance: activating it while it is already open returns
/// the existing process. Treating that as a launch, and then ending the process
/// when the session is deleted, closes an application other sessions are still
/// using — measured 2026-08-10 as a 5 test regression on the compatibility
/// suite, where a short-lived session's delete killed the long-lived one's
/// Calculator.
/// </para>
/// </param>
public sealed record LaunchedApplication(int ProcessId, nint WindowHandle, bool Started = true);

/// <summary>The outcome of a launch attempt.</summary>
/// <param name="Application">The running application, or null on failure.</param>
/// <param name="FailureMessage">
/// Why the launch failed, or null on success. A message rather than an
/// exception because it goes straight into a protocol fault, and because the
/// caller cannot ignore it: reading <paramref name="Application"/> without
/// checking gives a null the compiler objects to.
/// </param>
public sealed record LaunchResult(LaunchedApplication? Application, string? FailureMessage)
{
    /// <summary>A successful launch.</summary>
    /// <param name="application">The running application.</param>
    /// <returns>The result.</returns>
    public static LaunchResult Success(LaunchedApplication application) =>
        new(application, FailureMessage: null);

    /// <summary>A failed launch.</summary>
    /// <param name="message">Why it failed. Reported verbatim to the client.</param>
    /// <returns>The result.</returns>
    public static LaunchResult Failure(string message) =>
        new(Application: null, message);
}

/// <summary>
/// Starts applications and finds the window to drive.
/// </summary>
/// <remarks>
/// An interface so the protocol layer can be tested without a desktop. The
/// implementation being replaced launched processes inline in the route
/// handler, which meant no session test could run without a UI session and
/// none of them ever did.
/// </remarks>
public interface IApplicationLauncher
{
    /// <summary>Starts an application and waits for its window.</summary>
    /// <param name="target">What to launch.</param>
    /// <returns>The running application, or the reason it could not be started.</returns>
    LaunchResult Launch(ApplicationTarget target);
}

/// <summary>Ends an application this driver started.</summary>
/// <remarks>
/// <b>Separate from the launcher on purpose.</b> Launching and killing are
/// different authorities: a driver may attach to a window it did not start, and
/// terminating that process would close an application belonging to someone
/// else. Whether termination is allowed is decided by the session, not here.
/// </remarks>
public interface IApplicationTerminator
{
    /// <summary>Ends a session's application, gracefully if it will allow it.</summary>
    /// <param name="processId">The process the session tracked.</param>
    /// <param name="window">
    /// The session's window. Closed FIRST, and for some applications it is the
    /// only thing that may be closed at all.
    /// </param>
    /// <returns>True if the application is no longer present.</returns>
    /// <remarks>
    /// <para>
    /// <b>The window is not a convenience, it is a safety requirement.</b> A
    /// File Explorer window belongs to the long-running shell — the same
    /// <c>explorer.exe</c> that draws the desktop, taskbar and Start menu — so
    /// ending "the process that owns this window" would take the whole desktop
    /// with it. The window can be closed; the process must not be.
    /// </para>
    /// <para>
    /// The suite reaches this every run: it opens Explorer windows and expects
    /// <c>DELETE /session</c> to deal with them. Measured 2026-08-11: six or
    /// seven survived a single run.
    /// </para>
    /// </remarks>
    bool Terminate(int processId, nint window);
}

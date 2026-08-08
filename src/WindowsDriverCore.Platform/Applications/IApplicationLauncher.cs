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
public sealed record LaunchedApplication(int ProcessId, nint WindowHandle);

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

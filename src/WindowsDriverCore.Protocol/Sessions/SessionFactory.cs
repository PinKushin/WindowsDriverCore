using System.Globalization;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;

namespace WindowsDriverCore.Protocol.Sessions;

/// <summary>The outcome of a session-creation attempt.</summary>
/// <param name="Session">The new session, or null when rejected.</param>
/// <param name="Fault">The fault to report, or null on success.</param>
/// <param name="Message">The message belonging to the fault.</param>
public sealed record SessionCreateResult(
    DriverSession? Session,
    WebDriverFault? Fault,
    string? Message);

/// <summary>
/// Turns capabilities into a live session.
/// </summary>
/// <remarks>
/// Separate from the route so the three ways a session can begin — launch an
/// application, attach to an existing window, or take the whole desktop — are
/// one decision in one place rather than branches inside a request handler.
/// </remarks>
public sealed class SessionFactory
{
    private const string MissingWindowMessage =
        "Cannot find active window specified by capabilities: appTopLevelWindow";

    private readonly IApplicationLauncher _launcher;
    private readonly IWindowLocator _windows;

    /// <summary>Creates the factory.</summary>
    /// <param name="launcher">Starts applications.</param>
    /// <param name="windows">Answers questions about windows.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SessionFactory(IApplicationLauncher launcher, IWindowLocator windows)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(windows);

        _launcher = launcher;
        _windows = windows;
    }

    /// <summary>Creates a session from validated capabilities.</summary>
    /// <param name="capabilities">The capabilities.</param>
    /// <returns>The session, or the fault to report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
    public SessionCreateResult Create(SessionCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (capabilities.AppTopLevelWindow is not null)
        {
            return AttachToWindow(capabilities);
        }

        return capabilities.IsDesktopSession
            ? TakeDesktop(capabilities)
            : LaunchApplication(capabilities);
    }

    /// <summary>
    /// A desktop session drives everything and owns nothing: no process is
    /// started, so it carries process id 0.
    /// </summary>
    private SessionCreateResult TakeDesktop(SessionCapabilities capabilities) =>
        Created(capabilities, processId: 0, _windows.DesktopWindow);

    private SessionCreateResult LaunchApplication(SessionCapabilities capabilities)
    {
        LaunchResult launched = _launcher.Launch(new ApplicationTarget(
            capabilities.App!,
            capabilities.AppArguments,
            capabilities.AppWorkingDirectory));

        if (launched.Application is null)
        {
            // The launcher's message goes through verbatim: it knows what
            // actually failed, and the client matches on the text.
            return new SessionCreateResult(
                Session: null,
                WebDriverFault.UnknownError,
                launched.FailureMessage);
        }

        return Created(capabilities, launched.Application.ProcessId, launched.Application.WindowHandle);
    }

    /// <summary>
    /// Attaches to a window that already exists, for applications with no
    /// conventional launch path or a startup too slow to wait on.
    /// </summary>
    private SessionCreateResult AttachToWindow(SessionCapabilities capabilities)
    {
        // Hexadecimal, as WinAppDriver documents ("0xB822E2"). Parsing it as
        // decimal is the plausible mistake and would silently address a
        // completely different window rather than failing.
        if (!nint.TryParse(
                capabilities.AppTopLevelWindow,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out nint handle)
            || !_windows.Exists(handle))
        {
            return new SessionCreateResult(
                Session: null,
                WebDriverFault.NoSuchWindow,
                MissingWindowMessage);
        }

        return Created(capabilities, _windows.GetOwningProcessId(handle), handle);
    }

    private static SessionCreateResult Created(
        SessionCapabilities capabilities,
        int processId,
        nint windowHandle) =>
        new(
            new DriverSession(
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant(),
                capabilities.Echo,
                processId,
                windowHandle),
            Fault: null,
            Message: null);
}

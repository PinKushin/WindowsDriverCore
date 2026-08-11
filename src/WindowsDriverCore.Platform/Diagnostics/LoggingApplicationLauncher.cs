using System.Diagnostics;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Platform.Diagnostics;

/// <summary>
/// Puts every launch attempt into the transcript, with its cost.
/// </summary>
/// <remarks>
/// <para>
/// <b>The elapsed time is the part that carries information.</b> The window
/// search has a ten-second timeout, so a launch that took nine seconds and a
/// launch that took forty milliseconds both return a handle and mean completely
/// different things — the first ran out and returned whatever it was holding.
/// Nothing in a <c>LaunchResult</c> says which happened, and three separate
/// claims about that search were credited to the wrong mechanism because the
/// only observable was the handle.
/// </para>
/// <para>
/// A decorator, so <c>ApplicationLauncher</c> keeps doing one thing and its
/// existing tests keep constructing it unchanged.
/// </para>
/// </remarks>
public sealed class LoggingApplicationLauncher : IApplicationLauncher
{
    private readonly IApplicationLauncher _inner;
    private readonly ILaunchLog _log;

    /// <summary>Wraps a launcher.</summary>
    /// <param name="inner">The launcher that does the work.</param>
    /// <param name="log">Where launches are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LoggingApplicationLauncher(IApplicationLauncher inner, ILaunchLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public LaunchResult Launch(ApplicationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        long began = Stopwatch.GetTimestamp();
        LaunchResult result;

        try
        {
            result = _inner.Launch(target);
        }
        catch (Exception exception)
        {
            _log.ApplicationLaunched(
                target.App,
                processId: 0,
                window: 0,
                windowClass: string.Empty,
                exception.GetType().Name,
                Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            throw;
        }

        // The app is logged and its ARGUMENTS are not. A command line is
        // caller-supplied text that can carry credentials — plenty of test
        // harnesses pass them that way — and it is not needed to diagnose a
        // launch that could not find its window.
        //
        // The window CLASS is, though. ApplicationFrameWindow against
        // Windows.UI.Core.CoreWindow is what separates "the frame answered" from
        // "a CoreWindow was held to the deadline and returned", and the handle
        // alone cannot show it — which is how three claims about the window
        // search were each credited to the wrong mechanism.
        nint window = result.Application?.WindowHandle ?? 0;

        _log.ApplicationLaunched(
            target.App,
            result.Application?.ProcessId ?? 0,
            window,
            window == 0 ? string.Empty : WindowLocator.ClassNameOf(window),
            result.FailureMessage ?? string.Empty,
            Stopwatch.GetElapsedTime(began).TotalMilliseconds);

        return result;
    }
}

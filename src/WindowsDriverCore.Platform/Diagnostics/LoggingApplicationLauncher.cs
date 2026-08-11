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
    /// <summary>
    /// At or beyond this, the window search spent its whole budget.
    /// </summary>
    /// <remarks>
    /// The waiter's own timeout is ten seconds. Slightly under it here, because
    /// the measured elapsed time includes activation and the poll interval, so an
    /// exhausted search reports a little OVER the timeout rather than exactly it.
    /// </remarks>
    private const double TimedOutAtMilliseconds = 9_500;

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
        double elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;

        // A WAIT THAT SPENT ITS WHOLE BUDGET NEVER SAW THE THING.
        //
        // Nothing the compatibility suite drives legitimately takes ten seconds,
        // so a launch that returns AT the timeout is not a slow application - it
        // is a search that never detected a window. Those are different defects
        // and they have opposite fixes: wait longer versus look somewhere else.
        //
        // Measured 2026-08-11, and read the wrong way round for an hour: a
        // transcript line saying "10241.6 ms" was taken as Alarms & Clock being
        // slow to start. It was not. The application was on screen the whole
        // time under process 4852 while the search hunted for a window owned by
        // 3024, which activation had returned. A second activation one second
        // later returned 4852 and took 790 ms.
        //
        // So the transcript says TIMED OUT rather than printing a number a
        // reader has to recognise as suspicious.
        string outcome = result.FailureMessage ?? string.Empty;
        if (outcome.Length == 0 && elapsed >= TimedOutAtMilliseconds)
        {
            outcome = "TIMED OUT - never detected a window (look for the wrong process, not a slow app)";
        }

        _log.ApplicationLaunched(
            target.App,
            result.Application?.ProcessId ?? 0,
            window,
            window == 0 ? string.Empty : WindowLocator.ClassNameOf(window),
            outcome,
            elapsed);

        return result;
    }
}

using System.Diagnostics;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Applications;

namespace WindowsDriverCore.Platform.Diagnostics;

/// <summary>
/// Writes down which process a session teardown ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>A safety record.</b> <c>DELETE /session</c> terminates the tracked process,
/// and for a packaged application the window's <i>owner</i> is
/// <c>ApplicationFrameHost</c> — a broker shared by every UWP window on the
/// machine. Aimed there, one session teardown closes all of them.
/// <c>SessionTracksTheAppNotTheBrokerTests</c> guards against that at test time;
/// this makes the pid legible at run time, on the machine where it happened.
/// </para>
/// <para>
/// The return value is recorded too. <c>Terminate</c> reports whether the process
/// is really gone, and a false there means a session ended while its application
/// kept running — which is how the next run inherits a warm application it did
/// not ask for and measures a re-attach as a cold launch.
/// </para>
/// </remarks>
public sealed class LoggingApplicationTerminator : IApplicationTerminator
{
    private readonly IApplicationTerminator _inner;
    private readonly ITerminationLog _log;

    /// <summary>Wraps a terminator.</summary>
    /// <param name="inner">The terminator that does the work.</param>
    /// <param name="log">Where terminations are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LoggingApplicationTerminator(IApplicationTerminator inner, ITerminationLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public bool Terminate(int processId, nint window)
    {
        long began = Stopwatch.GetTimestamp();

        try
        {
            bool ended = _inner.Terminate(processId, window);
            _log.ApplicationTerminated(
                processId, ended, Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            return ended;
        }
        catch
        {
            // Recorded as "not ended" and rethrown. A teardown that threw leaves
            // a process behind, and that is exactly the line worth having.
            _log.ApplicationTerminated(
                processId, ended: false, Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            throw;
        }
    }
}

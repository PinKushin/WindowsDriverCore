using System.Globalization;
using System.IO;

namespace WindowsDriverCore.Host.Diagnostics;

/// <summary>
/// Writes a local crash report before the process goes down.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a server run from a CLI, or from CI.</b> Nobody is necessarily
/// watching the console when it dies, so the report has to land somewhere
/// findable afterwards rather than depend on being caught on the way past. The
/// directory is fixed and the file name carries the timestamp, so a caller
/// prints one line — the exact path — and that is enough to find it again,
/// live or in an archived CI log.
/// </para>
/// <para>
/// <b>Local only, the same rule as the request transcript.</b> This writes a
/// text file and nothing else — no sink, no endpoint, no transport. What
/// happens to the file afterwards is the operator's choice, not this driver's.
/// </para>
/// <para>
/// <b>A managed report, not a memory dump.</b> This catches
/// <c>AppDomain.UnhandledException</c> and
/// <c>TaskScheduler.UnobservedTaskException</c> — the exceptions .NET itself
/// can hand back a message and a stack trace for. It cannot see a native
/// crash, a stack overflow, or anything the runtime dies from before managed
/// code runs again; that is a separate mechanism.
/// </para>
/// </remarks>
public sealed class CrashDumpWriter
{
    private readonly string _directory;
    private readonly TimeProvider _clock;

    /// <summary>Creates the writer.</summary>
    /// <param name="directory">Where reports are written. Created on first use.</param>
    /// <param name="clock">Supplies the timestamp in the file name and the report.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CrashDumpWriter(string directory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(clock);

        _directory = directory;
        _clock = clock;
    }

    /// <summary>
    /// The directory used when nothing else has been configured.
    /// </summary>
    /// <remarks>
    /// Under the per-user local app data folder, alongside where the request
    /// transcript would go if a path were not supplied for it — a place that
    /// exists on every Windows machine without asking for elevation.
    /// </remarks>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsDriverCore",
        "crashes");

    /// <summary>Writes a report for one exception.</summary>
    /// <param name="exception">What was caught.</param>
    /// <param name="isTerminating">
    /// Whether the process is going down because of this. <c>false</c> for
    /// <c>TaskScheduler.UnobservedTaskException</c>, which does not kill the
    /// server — a report that always claimed otherwise would mislead whoever
    /// reads it about whether the driver is still running.
    /// </param>
    /// <returns>The full path written, so the caller can print it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public string Write(Exception exception, bool isTerminating)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Directory.CreateDirectory(_directory);

        DateTimeOffset when = _clock.GetUtcNow();

        // Milliseconds included, not just seconds — two crashes in the same
        // second must not overwrite each other's report.
        string stamp = when.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        string path = Path.Combine(_directory, $"crash-{stamp}.log");

        string report = string.Join(
            Environment.NewLine,
            "WindowsDriverCore crash report",
            $"When: {when:O}",
            $"Terminating: {isTerminating}",
            $"OS: {Environment.OSVersion}",
            $".NET: {Environment.Version}",
            string.Empty,
            exception.ToString());

        File.WriteAllText(path, report);

        return path;
    }
}

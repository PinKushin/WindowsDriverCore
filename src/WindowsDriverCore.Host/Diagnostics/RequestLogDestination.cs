using System.IO;

namespace WindowsDriverCore.Host.Diagnostics;

/// <summary>
/// Where the request transcript is written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Console by default</b>, because that is what WinAppDriver does and its
/// transcript is the only reason its failures are readable. A path in
/// <see cref="PathEnvironmentVariable"/> sends it to a file instead.
/// </para>
/// <para>
/// <b>An environment variable rather than a command-line switch.</b> The
/// argument grammar is a compatibility contract: an existing suite points at this
/// driver with WinAppDriver's own arguments, and <c>ServerAddress</c> rejects
/// anything outside the documented forms on purpose. The port override is an
/// environment variable for exactly this reason and this follows it.
/// </para>
/// <para>
/// <b>Local only.</b> The destination is a console or a file on this machine.
/// There is no other option, here or anywhere under
/// <c>WindowsDriverCore.Diagnostics</c> — no sink, no endpoint, no transport — so
/// there is no configuration that could send a transcript off the machine.
/// </para>
/// </remarks>
public sealed class RequestLogDestination : IDisposable
{
    /// <summary>Environment variable naming a file to write the transcript to.</summary>
    public const string PathEnvironmentVariable = "WINDOWSDRIVERCORE_LOG";

    private readonly StreamWriter? _file;

    private RequestLogDestination(TextWriter writer, StreamWriter? file)
    {
        Writer = writer;
        _file = file;
    }

    /// <summary>Where lines go.</summary>
    public TextWriter Writer { get; }

    /// <summary>
    /// The resolved file path, or null when the transcript goes to the console.
    /// </summary>
    /// <remarks>
    /// Reported at startup so there is never a question of which file a run
    /// wrote to — a relative path resolves against the working directory, which
    /// is not always where the person who set the variable expected.
    /// </remarks>
    public string? Path { get; private init; }

    /// <summary>Chooses the destination from the environment.</summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The destination. Dispose it to close a file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public static RequestLogDestination Open(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string? configured = environment(PathEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return new RequestLogDestination(Console.Out, file: null);
        }

        string resolved = System.IO.Path.GetFullPath(configured);

        // Append, not truncate. Two runs against one path must not silently
        // erase the first: a transcript that vanishes on restart is worse than
        // none, because the absence reads as requests that never happened.
        //
        // AutoFlush, because a driver that crashes loses exactly the request that
        // killed it — the one worth having.
        StreamWriter file = new(resolved, append: true) { AutoFlush = true };

        return new RequestLogDestination(file, file) { Path = resolved };
    }

    /// <summary>Closes the file, if there is one.</summary>
    /// <remarks>
    /// <see cref="Console.Out"/> is never disposed: it belongs to the process,
    /// and closing it would silence everything else the host writes.
    /// </remarks>
    public void Dispose() => _file?.Dispose();
}

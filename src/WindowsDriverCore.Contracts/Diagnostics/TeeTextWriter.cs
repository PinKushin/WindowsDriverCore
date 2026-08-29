using System.IO;
using System.Text;

namespace WindowsDriverCore.Diagnostics;

/// <summary>
/// Writes everything to two writers.
/// </summary>
/// <remarks>
/// <para>
/// A decorator, so the transcript keeps going exactly where it went before —
/// console or file — while a copy also reaches the buffer that
/// <c>POST /log</c> serves. Neither the listener that produces the lines nor the
/// destination that consumes them has to know the other exists.
/// </para>
/// <para>
/// <b>The second writer's failure must not take the first's output with it.</b>
/// The console transcript is the one a person is watching; a buffer that throws
/// would otherwise silence it. So the primary is written FIRST and the secondary
/// is best-effort.
/// </para>
/// </remarks>
public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _primary;
    private readonly TextWriter _secondary;

    /// <summary>Creates a tee.</summary>
    /// <param name="primary">Where the output must go.</param>
    /// <param name="secondary">Where a copy should go, best-effort.</param>
    public TeeTextWriter(TextWriter primary, TextWriter secondary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);

        _primary = primary;
        _secondary = secondary;
    }

    /// <inheritdoc />
    public override Encoding Encoding => _primary.Encoding;

    /// <inheritdoc />
    public override void Write(char value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        _primary.Write(value);
        _secondary.Write(value);
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        _primary.WriteLine(value);
        _secondary.WriteLine(value);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        _primary.Flush();
        _secondary.Flush();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Neither writer is disposed here.</b> Both are owned by the composition
    /// root — the destination closes a log file, and the buffer outlives any one
    /// request. A decorator that disposed what it was handed would close the
    /// transcript file the first time anything disposed the tee.
    /// </remarks>
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

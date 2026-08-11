using System.Diagnostics;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Automation.Diagnostics;

/// <summary>
/// Records how large a page-source read was.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added after arguing it was redundant, and the argument was wrong.</b> A
/// <c>GET /source</c> request is almost entirely the read, so the request line
/// does carry the cost — but not the document size, and that is the part with
/// information in it. During the 2026-08-11 investigation the numbers that
/// mattered were 19,656 characters before a click and 37,413 after: the dialog
/// opening, visible as nothing else.
/// </para>
/// <para>
/// A null document means the window is gone, and it is reported as <c>-1</c>
/// rather than as zero. An empty document is a real answer for a window with
/// nothing in it, and collapsing the two would make a dead session look like an
/// empty one.
/// </para>
/// </remarks>
public sealed class LoggingPageSourceReader : IPageSourceReader
{
    /// <summary>Reported when the window no longer exists.</summary>
    private const int NoWindow = -1;

    private readonly IPageSourceReader _inner;
    private readonly IPageSourceLog _log;

    /// <summary>Wraps a reader.</summary>
    /// <param name="inner">The reader that does the work.</param>
    /// <param name="log">Where reads are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LoggingPageSourceReader(IPageSourceReader inner, IPageSourceLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public string? Source(nint window)
    {
        long began = Stopwatch.GetTimestamp();

        try
        {
            string? document = _inner.Source(window);

            _log.PageSourceRead(
                document?.Length ?? NoWindow,
                Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            return document;
        }
        catch
        {
            _log.PageSourceRead(NoWindow, Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            throw;
        }
    }
}

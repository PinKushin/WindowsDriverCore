using System.Threading;
using System.IO;
using System.Text;

namespace WindowsDriverCore.Diagnostics;

/// <summary>One line of the transcript, as <c>POST /log</c> reports it.</summary>
/// <param name="Timestamp">Unix milliseconds, which is what the protocol uses.</param>
/// <param name="Level">Always <c>INFO</c>; see <see cref="LogBuffer"/>.</param>
/// <param name="Message">The transcript line.</param>
public sealed record LogEntry(long Timestamp, string Level, string Message);

/// <summary>
/// Keeps the most recent transcript lines so a client can ask for them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A <see cref="TextWriter"/> so it composes rather than duplicates.</b> The
/// transcript is already produced by <c>TextRequestLogListener</c> writing
/// formatted lines to a writer; this is another writer to send them to. Parsing
/// the events a second time would be a second implementation of the same
/// question, which is how WinAppDriver's own XPath singular and plural drifted
/// apart into issue #1079.
/// </para>
/// <para>
/// <b>What that composition also buys is the privacy rule, for free.</b>
/// Locators are logged; <c>SetValue</c> and <c>SendKeys</c> arguments never are,
/// because <c>IInteractionLog</c> has no parameter that could carry one. Feeding
/// this endpoint from the SAME formatted lines means a log served over HTTP
/// cannot contain a password that the console transcript would not — and a
/// second, independent reader of the raw events could have.
/// </para>
/// <para>
/// <b>Bounded, and it drops the OLDEST.</b> A driver can run for days; an
/// unbounded buffer is a leak with a slow fuse. Dropping the oldest keeps the
/// lines nearest a failure, which are the ones anybody asking for a log wants.
/// </para>
/// <para>
/// <b>Draining is the protocol's semantics, not a convenience.</b>
/// <c>POST /log</c> returns entries SINCE THE LAST CALL and Selenium relies on
/// it: a client polling a log that never drains re-reads its whole history every
/// time and reports each line again.
/// </para>
/// </remarks>
public sealed class LogBuffer : TextWriter
{
    /// <summary>How many lines to keep.</summary>
    /// <remarks>
    /// A session's worth of transcript with room to spare — a full
    /// compatibility-suite run produces on the order of a few thousand lines
    /// across 290 tests, so this holds the recent past rather than the whole run.
    /// </remarks>
    public const int Capacity = 5000;

    /// <summary>The only level this driver emits.</summary>
    /// <remarks>
    /// The transcript is one severity: it records what happened, and a failure is
    /// visible in the line rather than in a level beside it. Reporting a made-up
    /// mix of INFO and WARNING would be inventing structure the source does not
    /// have.
    /// </remarks>
    public const string Level = "INFO";

    private readonly Queue<LogEntry> _lines = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly StringBuilder _pending = new();

    /// <summary>Creates a buffer.</summary>
    /// <param name="clock">Stamps each line.</param>
    public LogBuffer(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <inheritdoc />
    /// <remarks>
    /// <b>Character by character, because that is what a <c>TextWriter</c>
    /// guarantees.</b> Every other overload funnels here by contract, so
    /// buffering at this level catches a caller that writes a line one char at a
    /// time as well as one that writes it whole. A newline completes an entry.
    /// </remarks>
    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                Complete();
                return;
            }

            // Carriage returns are dropped rather than kept: the line is stored
            // as data, and a trailing \r would show up inside a JSON string.
            if (value != '\r')
            {
                _pending.Append(value);
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        // Not delegated to the base, which would loop over Write(char) and take
        // the lock once per character. One lock per string.
        lock (_gate)
        {
            foreach (char character in value)
            {
                if (character == '\n')
                {
                    Complete();
                }
                else if (character != '\r')
                {
                    _pending.Append(character);
                }
            }
        }
    }

    /// <summary>Takes everything buffered and clears it.</summary>
    /// <returns>The entries, oldest first.</returns>
    /// <remarks>
    /// Draining is the protocol's contract for <c>POST /log</c>, not a
    /// convenience: a client polls it, and one that re-read its whole history
    /// each time would report every line again on every call.
    /// </remarks>
    public IReadOnlyList<LogEntry> Drain()
    {
        lock (_gate)
        {
            LogEntry[] taken = [.. _lines];
            _lines.Clear();
            return taken;
        }
    }

    /// <summary>Completes the pending line. The lock is already held.</summary>
    private void Complete()
    {
        if (_pending.Length == 0)
        {
            // A blank line is not an entry. The transcript separates sections
            // with them, and forwarding them would pad a client's log with
            // empty records it has to filter.
            return;
        }

        if (_lines.Count == Capacity)
        {
            _lines.Dequeue();
        }

        _lines.Enqueue(new LogEntry(
            _clock.GetUtcNow().ToUnixTimeMilliseconds(), Level, _pending.ToString()));

        _pending.Clear();
    }
}

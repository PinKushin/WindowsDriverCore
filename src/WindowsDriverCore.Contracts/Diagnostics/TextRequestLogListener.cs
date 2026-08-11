using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Threading;

namespace WindowsDriverCore.Diagnostics;

/// <summary>
/// Transcribes the driver's request events as one line each.
/// </summary>
/// <remarks>
/// <para>
/// <b>The consumer that makes the instrument useful.</b> An
/// <see cref="EventSource"/> publishes and nothing more; without something
/// attached, the events go nowhere. WinAppDriver prints its transcript to its
/// console and that is the only reason its failures are readable, so this driver
/// does the same.
/// </para>
/// <para>
/// <b>It writes where it is told and nowhere else.</b> The destination is a
/// <see cref="TextWriter"/> supplied by the caller — console or file. There is no
/// network code here and none anywhere under
/// <see cref="DriverEventSource"/>, so no configuration mistake can turn this
/// into something that sends a user's data off their machine.
/// </para>
/// </remarks>
public sealed class TextRequestLogListener : EventListener
{
    /// <summary>Rendered in place of <see cref="IRequestLog.NoJsonWireStatus"/>.</summary>
    /// <remarks>
    /// The sentinel must not reach a reader. <c>-1</c> in a transcript reads as a
    /// protocol status, and the protocol defines no negative statuses, so it
    /// would be a value that looks meaningful and is not.
    /// </remarks>
    private const string NoEnvelope = "-";

    private readonly TextWriter _writer;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    /// <summary>Creates the listener and subscribes it.</summary>
    /// <param name="writer">Where lines are written.</param>
    /// <param name="clock">Supplies the timestamp on each line.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <b>Field assignment happens after the base constructor runs, which already
    /// calls back into this object.</b> <see cref="EventListener"/> enumerates
    /// every source that already exists and raises
    /// <see cref="OnEventSourceCreated"/> from its own constructor, at which point
    /// <c>_writer</c> is still null. That is why the override touches no field —
    /// and why <see cref="OnEventWritten"/> guards anyway rather than trusting the
    /// ordering to stay as it is.
    /// </remarks>
    public TextRequestLogListener(TextWriter writer, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);

        _writer = writer;
        _clock = clock;
    }

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);

        // ONLY this driver's source. A listener sees every EventSource in the
        // process — the runtime's own, and any library the host loads — and
        // subscribing broadly would bury the request transcript in noise.
        if (eventSource.Name == DriverEventSource.SourceName)
        {
            EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
        }
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (_writer is null ||
            eventData.EventId != DriverEventSource.RequestCompletedEventId ||
            eventData.Payload is not { Count: 5 } payload)
        {
            return;
        }

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{_clock.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} " +
            $"{payload[0]} {payload[1]} -> {payload[2]} " +
            $"jwp {Envelope(payload[3])} {Cost(payload[4])} ms");

        // Locked because EventListener delivers on whichever thread wrote the
        // event, and Kestrel serves requests on many. A TextWriter is not
        // thread-safe, and interleaved lines in a transcript are worse than a
        // missing one: they read as real requests that never happened.
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private static string Envelope(object? status) =>
        status is int value && value != IRequestLog.NoJsonWireStatus
            ? value.ToString(CultureInfo.InvariantCulture)
            : NoEnvelope;

    private static string Cost(object? elapsed) =>
        elapsed is double value
            ? value.ToString("F1", CultureInfo.InvariantCulture)
            : "?";
}

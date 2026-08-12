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

        if (_writer is null || eventData.Payload is not { } payload)
        {
            return;
        }

        string? body = Describe(eventData.EventId, payload);
        if (body is null)
        {
            return;
        }

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{_clock.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} {body}");

        // Locked because EventListener delivers on whichever thread wrote the
        // event, and Kestrel serves requests on many. A TextWriter is not
        // thread-safe, and interleaved lines in a transcript are worse than a
        // missing one: they read as real requests that never happened.
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Renders one event, or null when it is not one this transcript carries.
    /// </summary>
    /// <remarks>
    /// <b>The payload count is checked per event id, not once.</b> Reading
    /// positionally from a payload whose shape has changed would render garbage
    /// rather than fail, and a transcript that lies is worse than one with a gap:
    /// the gap is visible.
    /// </remarks>
    private static string? Describe(int eventId, System.Collections.ObjectModel.ReadOnlyCollection<object?> payload) =>
        (eventId, payload.Count) switch
        {
            (DriverEventSource.RequestCompletedEventId, 5) => string.Create(
                CultureInfo.InvariantCulture,
                $"{payload[0]} {payload[1]} -> {payload[2]} " +
                $"jwp {Envelope(payload[3])} {Cost(payload[4])} ms"),

            (DriverEventSource.FindCompletedEventId, 5) => string.Create(
                CultureInfo.InvariantCulture,
                $"  find {payload[0]}='{payload[1]}' -> {payload[2]} match(es)" +
                $"{Because(payload[3])} {Cost(payload[4])} ms"),

            (DriverEventSource.ElementActionCompletedEventId, 4) => string.Create(
                CultureInfo.InvariantCulture,
                $"  {payload[0]} -> {payload[1]}{Via(payload[2])} {Cost(payload[3])} ms"),

            (DriverEventSource.ApplicationLaunchedEventId, 6) => string.Create(
                CultureInfo.InvariantCulture,
                $"  launch '{payload[0]}' -> pid {payload[1]} window 0x{Handle(payload[2])}" +
                $"{Kind(payload[3])}{Because(payload[4])} {Cost(payload[5])} ms"),

            (DriverEventSource.KeysDispatchedEventId, 2) => string.Create(
                CultureInfo.InvariantCulture,
                $"  keys -> {(payload[0] is true ? "raised" : "NOT RAISED, went to whatever had focus")}" +
                $" {Cost(payload[1])} ms"),

            // "DID NOT WAIT" is the line worth reading, and it is spelled loudly
            // for the same reason "STILL THERE" is below: the request answers
            // success either way, so a silent failure is invisible on the wire.
            (DriverEventSource.InputDrainedEventId, 2) => string.Create(
                CultureInfo.InvariantCulture,
                $"  drain -> {(payload[0] is true ? "waited" : "DID NOT WAIT, the read may race the typing")}" +
                $" {Cost(payload[1])} ms"),

            (DriverEventSource.WindowClosedEventId, 3) => string.Create(
                CultureInfo.InvariantCulture,
                $"  close window 0x{Handle(payload[0])} -> " +
                $"{(payload[1] is true ? "gone" : "STILL THERE")} {Cost(payload[2])} ms"),

            (DriverEventSource.ApplicationTerminatedEventId, 3) => string.Create(
                CultureInfo.InvariantCulture,
                $"  terminate pid {payload[0]} -> " +
                $"{(payload[1] is true ? "ended" : "STILL RUNNING")} {Cost(payload[2])} ms"),

            (DriverEventSource.ElementResolvedEventId, 2) => string.Create(
                CultureInfo.InvariantCulture,
                $"    resolve -> {payload[0]} {Cost(payload[1])} ms"),

            (DriverEventSource.PageSourceReadEventId, 2) => string.Create(
                CultureInfo.InvariantCulture,
                $"  source -> {Document(payload[0])} {Cost(payload[1])} ms"),

            (DriverEventSource.PointerTargetedEventId, 6) => string.Create(
                CultureInfo.InvariantCulture,
                $"  {payload[0]} -> ({payload[1]},{payload[2]})" +
                $"{Rectangle(payload[3], payload[4])} {Cost(payload[5])} ms"),

            _ => null,
        };

    /// <summary>
    /// The element a point was computed from, when there was one.
    /// </summary>
    /// <remarks>
    /// <b>Absent and empty must not read the same.</b> A command with no element
    /// shows no size at all; an element UIA could see but could not place shows
    /// <c>NO RECTANGLE</c>, because its centre is <c>(0,0)</c> and that looks
    /// like an ordinary coordinate on its own. Printing <c>0x0</c> would be
    /// accurate and would still let a reader skim past it.
    /// </remarks>
    private static string Rectangle(object? width, object? height) =>
        (width, height) switch
        {
            (int w, int h) when w < 0 || h < 0 => string.Empty,
            (0, 0) => " of NO RECTANGLE",
            (int w, int h) => string.Create(CultureInfo.InvariantCulture, $" of {w}x{h}"),
            _ => string.Empty,
        };

    private static string Document(object? characters) =>
        characters is int length && length >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{length} chars")
            : "NO WINDOW";

    private static string Kind(object? windowClass) =>
        windowClass is string name && name.Length > 0 ? $" ({name})" : string.Empty;

    private static string Because(object? failure) =>
        failure is string reason && reason.Length > 0 ? $" FAILED: {reason}" : string.Empty;

    private static string Via(object? path) =>
        path is string rung && rung.Length > 0 ? $" via {rung}" : string.Empty;

    private static string Handle(object? window) =>
        window is long value
            ? value.ToString("X", CultureInfo.InvariantCulture)
            : "?";

    private static string Envelope(object? status) =>
        status is int value && value != IRequestLog.NoJsonWireStatus
            ? value.ToString(CultureInfo.InvariantCulture)
            : NoEnvelope;

    private static string Cost(object? elapsed) =>
        elapsed is double value
            ? value.ToString("F1", CultureInfo.InvariantCulture)
            : "?";
}

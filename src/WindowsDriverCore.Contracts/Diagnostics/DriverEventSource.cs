using System.Diagnostics.Tracing;

namespace WindowsDriverCore.Diagnostics;

/// <summary>
/// The driver's diagnostic events, published through <see cref="EventSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why EventSource and not a logging framework.</b> It is in the BCL, so it
/// adds no package and no supply-chain surface. When nothing is listening a
/// <c>WriteEvent</c> call checks one enabled flag and returns, so an always-on
/// instrument costs effectively nothing on the hot path — which is the whole
/// premise of a driver whose selling point is that it is faster than the one it
/// replaces. On Windows the events reach ETW; elsewhere they reach EventPipe.
/// </para>
/// <para>
/// <b>Nothing here goes anywhere.</b> An <c>EventSource</c> publishes in-process
/// and a consumer has to attach; there is no sink, no endpoint and no transport
/// in this assembly, so there is nothing to misconfigure into sending a user's
/// data off their machine. <see cref="IRequestLog"/> additionally has no
/// parameter that could carry a payload.
/// </para>
/// <para>
/// <b>Not a static singleton.</b> The conventional <c>public static readonly Log</c>
/// field is exactly the pattern this project bans from anything a route handler
/// can reach. It is registered as a DI singleton instead, which is the same one
/// instance with the substitutability kept.
/// </para>
/// </remarks>
[EventSource(Name = SourceName)]
public sealed class DriverEventSource
    : EventSource, IRequestLog, IFindLog, IInteractionLog, ILaunchLog, ITerminationLog, IResolveLog,
      IPageSourceLog, IPointerLog
{
    /// <summary>
    /// The ETW/EventPipe provider name a consumer subscribes to.
    /// </summary>
    /// <remarks>
    /// Dashed rather than dotted. A dotted name is legal but ETW treats the
    /// leading segment as a vendor prefix by convention, and this is not
    /// Microsoft's.
    /// </remarks>
    public const string SourceName = "PinKushin-WindowsDriverCore";

    /// <summary>Event id for <see cref="RequestCompleted"/>.</summary>
    /// <remarks>
    /// Ids are part of the wire contract for anyone consuming these events, so
    /// they are named constants and are never renumbered — a new event takes the
    /// next free id.
    /// </remarks>
    public const int RequestCompletedEventId = 1;

    /// <summary>Event id for <see cref="FindCompleted"/>.</summary>
    public const int FindCompletedEventId = 2;

    /// <summary>Event id for <see cref="ElementActionCompleted"/>.</summary>
    public const int ElementActionCompletedEventId = 3;

    /// <summary>Event id for <see cref="ApplicationLaunched"/>.</summary>
    public const int ApplicationLaunchedEventId = 4;

    /// <summary>Event id for <see cref="ApplicationTerminated"/>.</summary>
    public const int ApplicationTerminatedEventId = 5;

    /// <summary>Event id for <see cref="ElementResolved"/>.</summary>
    public const int ElementResolvedEventId = 6;

    /// <summary>Event id for <see cref="PageSourceRead"/>.</summary>
    public const int PageSourceReadEventId = 7;

    /// <summary>Event id for <see cref="PointerTargeted"/>.</summary>
    public const int PointerTargetedEventId = 8;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The <see cref="EventSource.IsEnabled()"/> guard is the whole cost
    /// argument.</b> This signature has no fast <c>WriteEvent</c> overload, so it
    /// falls through to the <c>params object[]</c> one, which boxes five
    /// arguments and allocates an array. That is a fine price for a request that
    /// somebody asked to see, and an unacceptable one on every request when
    /// nobody is listening. The guard reduces the disabled case to a volatile
    /// read.
    /// </para>
    /// <para>
    /// Not yet optimised further, and deliberately so: <c>WriteEventCore</c> with
    /// a stack-allocated <c>EventData*</c> removes the allocation, but it needs
    /// <c>AllowUnsafeBlocks</c> in this otherwise-minimal project and it is only
    /// worth it if the enabled path shows up in a measurement. Left for a
    /// benchmark to decide rather than assumed.
    /// </para>
    /// </remarks>
    [Event(
        RequestCompletedEventId,
        Level = EventLevel.Informational,
        Message = "{0} {1} -> HTTP {2}, status {3}, {4} ms")]
    public void RequestCompleted(
        string method,
        string route,
        int httpStatus,
        int jsonWireStatus,
        double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(
            RequestCompletedEventId,
            method,
            route,
            httpStatus,
            jsonWireStatus,
            elapsedMilliseconds);
    }

    /// <inheritdoc />
    [Event(
        FindCompletedEventId,
        Level = EventLevel.Informational,
        Message = "find {0}='{1}' -> {2} match(es) {3} {4} ms")]
    public void FindCompleted(
        string locatorKind,
        string locatorValue,
        int matches,
        string failure,
        double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(
            FindCompletedEventId, locatorKind, locatorValue, matches, failure, elapsedMilliseconds);
    }

    /// <inheritdoc />
    [Event(
        ElementActionCompletedEventId,
        Level = EventLevel.Informational,
        Message = "{0} -> {1} via {2} {3} ms")]
    public void ElementActionCompleted(
        string action,
        string outcome,
        string path,
        double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(ElementActionCompletedEventId, action, outcome, path, elapsedMilliseconds);
    }

    /// <inheritdoc />
    [Event(
        ApplicationLaunchedEventId,
        Level = EventLevel.Informational,
        Message = "launch '{0}' -> pid {1} window 0x{2:X} {3} {4} {5} ms")]
    public void ApplicationLaunched(
        string app,
        int processId,
        long window,
        string windowClass,
        string failure,
        double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(
            ApplicationLaunchedEventId,
            app,
            processId,
            window,
            windowClass,
            failure,
            elapsedMilliseconds);
    }

    /// <inheritdoc />
    [Event(
        ApplicationTerminatedEventId,
        Level = EventLevel.Informational,
        Message = "terminate pid {0} -> ended {1} {2} ms")]
    public void ApplicationTerminated(int processId, bool ended, double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(ApplicationTerminatedEventId, processId, ended, elapsedMilliseconds);
    }

    /// <inheritdoc />
    [Event(
        ElementResolvedEventId,
        Level = EventLevel.Informational,
        Message = "resolve -> {0} {1} ms")]
    public void ElementResolved(string outcome, double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(ElementResolvedEventId, outcome, elapsedMilliseconds);
    }

    /// <inheritdoc />
    [Event(
        PageSourceReadEventId,
        Level = EventLevel.Informational,
        Message = "source -> {0} chars {1} ms")]
    public void PageSourceRead(int characters, double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(PageSourceReadEventId, characters, elapsedMilliseconds);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Six arguments, so there is no fast <c>WriteEvent</c> overload and this
    /// takes the params array path — the same trade
    /// <see cref="RequestCompleted"/> makes, and for the same reason: a pointer
    /// command is one event per dispatched input, not a per-frame stream.
    /// </remarks>
    [Event(
        PointerTargetedEventId,
        Level = EventLevel.Informational,
        Message = "{0} -> ({1},{2}) of {3}x{4} {5} ms")]
    public void PointerTargeted(
        string command,
        int x,
        int y,
        int width,
        int height,
        double elapsedMilliseconds)
    {
        if (!IsEnabled())
        {
            return;
        }

        WriteEvent(PointerTargetedEventId, command, x, y, width, height, elapsedMilliseconds);
    }
}

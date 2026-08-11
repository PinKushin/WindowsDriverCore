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
public sealed class DriverEventSource : EventSource, IRequestLog
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
}

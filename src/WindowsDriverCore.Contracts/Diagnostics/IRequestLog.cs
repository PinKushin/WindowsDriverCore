namespace WindowsDriverCore.Diagnostics;

/// <summary>
/// Records that a request finished, and what it cost.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no parameter for a request or response body, and that is the
/// design.</b> A driver's payloads carry whatever the suite under test typed —
/// <c>POST /session/:id/element/:id/value</c> is how a login test sends a
/// password. Redacting bodies would mean a correct redactor standing between a
/// secret and a log file; not accepting them means no code path exists that could
/// write one. The second is a property of the shape rather than of the
/// implementation, so it cannot regress.
/// </para>
/// <para>
/// Narrow on purpose (interface segregation): a consumer that only needs to know
/// a request happened should not have to implement anything else, and every
/// widening of this interface is a new decision about what may be written to
/// disk.
/// </para>
/// <para>
/// <b>An interface, not a static.</b> <c>EventSource</c> is conventionally a
/// static singleton and this project forbids statics reachable from a route
/// handler, because that is what made the previous implementation untestable.
/// The implementation is registered as a DI singleton instead: same one instance,
/// substitutable in a test.
/// </para>
/// </remarks>
public interface IRequestLog
{
    /// <summary>Records a finished request.</summary>
    /// <param name="method">HTTP method, e.g. <c>POST</c>.</param>
    /// <param name="route">
    /// The request path. Element and session ids ride in it and are deliberately
    /// kept: they are what makes a transcript followable, and they are opaque
    /// identifiers this driver issued rather than anything the caller typed.
    /// </param>
    /// <param name="httpStatus">The HTTP status code sent.</param>
    /// <param name="jsonWireStatus">
    /// The JSON Wire Protocol status in the response envelope; <c>0</c> is
    /// success. Logged separately from <paramref name="httpStatus"/> because JWP
    /// reports most faults as HTTP 200 with a non-zero status, so the HTTP code
    /// alone cannot distinguish a working command from a failing one.
    /// </param>
    /// <param name="elapsedMilliseconds">Wall-clock cost of the request.</param>
    void RequestCompleted(
        string method,
        string route,
        int httpStatus,
        int jsonWireStatus,
        double elapsedMilliseconds);
}

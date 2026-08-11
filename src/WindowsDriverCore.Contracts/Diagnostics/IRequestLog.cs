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
    /// <summary>
    /// Reported as the JSON Wire status when the response carries no envelope.
    /// </summary>
    /// <remarks>
    /// <c>GET /status</c> is the one response with no <c>status</c> field at all.
    /// Reporting <c>0</c> for it would read as "succeeded with status 0" and be
    /// indistinguishable from a real success envelope, so absence gets its own
    /// value. Negative because every real JSON Wire status is non-negative.
    /// </remarks>
    const int NoJsonWireStatus = -1;

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
    /// success and <see cref="NoJsonWireStatus"/> means there was no envelope.
    /// Logged separately from <paramref name="httpStatus"/> because the two are
    /// not derivable from each other: in this driver's own fault table HTTP 404
    /// covers status <c>7</c> (no such element) and status <c>9</c> (unknown
    /// command), and HTTP 400 covers <c>10</c>, <c>23</c>, <c>100</c> and
    /// <c>105</c>. The HTTP code alone cannot tell "the element was not there"
    /// from "that route does not exist".
    /// </param>
    /// <param name="elapsedMilliseconds">Wall-clock cost of the request.</param>
    void RequestCompleted(
        string method,
        string route,
        int httpStatus,
        int jsonWireStatus,
        double elapsedMilliseconds);
}

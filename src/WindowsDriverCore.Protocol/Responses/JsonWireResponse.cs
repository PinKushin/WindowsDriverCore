using WindowsDriverCore.Protocol.Errors;

namespace WindowsDriverCore.Protocol.Responses;

/// <summary>
/// Builds JSON Wire Protocol response envelopes.
/// </summary>
/// <remarks>
/// WinAppDriver uses five distinct shapes and the differences are observable:
/// a void command omits <c>value</c> rather than sending null, <c>/sessions</c>
/// and <c>DELETE /session</c> omit <c>sessionId</c>, and <c>/status</c> has no
/// envelope at all. Routing every response through here keeps that in one place
/// rather than leaving each handler to reinvent it, which is how the previous
/// implementation ended up emitting two different shapes from code that read
/// identically at the call site.
/// </remarks>
public static class JsonWireResponse
{
    /// <summary>The status value every successful response carries.</summary>
    public const int SuccessStatus = 0;

    /// <summary>A session-scoped command that returns a value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="sessionId">The session id.</param>
    /// <param name="value">The result.</param>
    /// <returns>The envelope.</returns>
    public static SessionResponse<T> ForSession<T>(string sessionId, T value) =>
        new(sessionId, SuccessStatus, value);

    /// <summary>A session-scoped command that returns nothing.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The envelope, carrying no <c>value</c> property.</returns>
    public static VoidSessionResponse ForSessionVoid(string sessionId) =>
        new(sessionId, SuccessStatus);

    /// <summary>A command not scoped to a session that returns a value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The result.</param>
    /// <returns>The envelope, carrying no <c>sessionId</c>.</returns>
    public static ServerResponse<T> ForServer<T>(T value) =>
        new(SuccessStatus, value);

    /// <summary>A command not scoped to a session that returns nothing.</summary>
    /// <returns>An envelope carrying only <c>status</c>.</returns>
    public static VoidServerResponse ForServerVoid() =>
        new(SuccessStatus);

    /// <summary>A failure.</summary>
    /// <param name="fault">The fault, supplying the status and error string.</param>
    /// <param name="message">
    /// The message for this call site. Kept separate from the fault because the
    /// same fault carries different messages depending on cause.
    /// </param>
    /// <returns>The envelope, carrying no <c>sessionId</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fault"/> is null.</exception>
    public static FaultResponse ForFault(WebDriverFault fault, string message)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return new FaultResponse(fault.Status, new FaultDetail(fault.Error, message));
    }
}

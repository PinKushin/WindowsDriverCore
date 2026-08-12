using System.Text.Json.Serialization;

namespace WindowsDriverCore.Protocol.Responses;

/// <summary>
/// A W3C WebDriver response body.
/// </summary>
/// <remarks>
/// <para>
/// <b>W3C carries only <c>value</c>.</b> No <c>status</c> integer and no
/// <c>sessionId</c> beside it — success is the HTTP status code, and the session
/// id travels inside <c>value</c> on session creation and nowhere else. A JSON
/// Wire client reads <c>status</c> to decide whether a call worked, so the two
/// shapes cannot be merged into one body that satisfies both.
/// </para>
/// <para>
/// It still implements <see cref="IJsonWireEnvelope"/> so the request transcript
/// keeps reporting a status for every response, whichever dialect answered. The
/// log is about what happened, not about what the client was told.
/// </para>
/// </remarks>
/// <param name="Value">Whatever the command produced. Null for a void command.</param>
/// <param name="Status">The JSON Wire status, for the transcript only.</param>
public sealed record W3CResponse(
    [property: JsonPropertyName("value")] object? Value,
    [property: JsonIgnore] int Status) : IJsonWireEnvelope;

/// <summary>
/// The <c>value</c> of a failed W3C response.
/// </summary>
/// <remarks>
/// <para>
/// <b>The error is a STRING, not a number.</b> W3C names each failure
/// (<c>no such element</c>, <c>stale element reference</c>) where JSON Wire uses
/// an integer, and a Selenium 4 client maps that string to the exception it
/// raises. Sending the number under a W3C shape produces a client that reports
/// "unknown error" for everything.
/// </para>
/// <para>
/// <c>stacktrace</c> is required by the specification and is deliberately empty:
/// this driver has a stack, but it is OUR stack, and putting it in a protocol
/// response tells the caller about our internals rather than about their test.
/// </para>
/// </remarks>
/// <param name="Error">The W3C error name.</param>
/// <param name="Message">The human-readable message, identical to the JWP one.</param>
/// <param name="StackTrace">Required by the specification, always empty here.</param>
public sealed record W3CFault(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("stacktrace")] string StackTrace);

/// <summary>
/// The <c>value</c> of a successful W3C <c>POST /session</c>.
/// </summary>
/// <param name="SessionId">The new session.</param>
/// <param name="Capabilities">What the session was created with.</param>
public sealed record W3CSessionCreated(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("capabilities")] object Capabilities);

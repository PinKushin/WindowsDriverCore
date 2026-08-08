using System.Text.Json.Serialization;

namespace WindowsDriverCore.Protocol.Responses;

/// <summary>A reference to an element, as the wire spells it.</summary>
/// <param name="Element">The element id, a dot-separated UIA RuntimeId.</param>
/// <remarks>
/// The property really is upper-case <c>ELEMENT</c>: that is the JSON Wire
/// Protocol spelling, and Selenium 3 reads exactly that key.
/// </remarks>
public sealed record ElementReference(
    [property: JsonPropertyName("ELEMENT")] string Element);

/// <summary>A response to a session-scoped command that returns a value.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="SessionId">The session the command ran against.</param>
/// <param name="Status">Always 0; a failure uses <see cref="FaultResponse"/>.</param>
/// <param name="Value">The result of the command.</param>
public sealed record SessionResponse<T>(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("value")] T Value);

/// <summary>A response to a session-scoped command that returns nothing.</summary>
/// <param name="SessionId">The session the command ran against.</param>
/// <param name="Status">Always 0.</param>
/// <remarks>Carries no <c>value</c> property at all, matching WinAppDriver.</remarks>
public sealed record VoidSessionResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] int Status);

/// <summary>A response to a command not scoped to a session, such as <c>/sessions</c>.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Status">Always 0.</param>
/// <param name="Value">The result of the command.</param>
public sealed record ServerResponse<T>(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("value")] T Value);

/// <summary>A response carrying only a status, as <c>DELETE /session</c> does.</summary>
/// <param name="Status">Always 0.</param>
public sealed record VoidServerResponse(
    [property: JsonPropertyName("status")] int Status);

/// <summary>The <c>value</c> of a failure response.</summary>
/// <param name="Error">The error string belonging to the fault.</param>
/// <param name="Message">
/// The message for this particular call site. Not derivable from the fault:
/// status 23 carries a different message for a closed window than for a handle
/// that was never valid.
/// </param>
public sealed record FaultDetail(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);

/// <summary>A failure response. Carries no <c>sessionId</c>.</summary>
/// <param name="Status">The numeric status belonging to the fault.</param>
/// <param name="Value">The error and message.</param>
public sealed record FaultResponse(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("value")] FaultDetail Value);

/// <summary>Build metadata reported by <c>GET /status</c>.</summary>
/// <param name="Version">Driver version.</param>
/// <param name="Revision">Build revision.</param>
/// <param name="Time">Build timestamp.</param>
public sealed record BuildInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("time")] string Time);

/// <summary>Host metadata reported by <c>GET /status</c>.</summary>
/// <param name="Arch">Processor architecture, for example <c>amd64</c>.</param>
/// <param name="Name">Always <c>windows</c>.</param>
/// <param name="Version">Operating system version string.</param>
public sealed record OperatingSystemInfo(
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

/// <summary>The <c>GET /status</c> body.</summary>
/// <param name="Build">Build metadata.</param>
/// <param name="Os">Host metadata.</param>
/// <remarks>
/// The one response with no envelope: no <c>status</c> field and no
/// <c>value</c> wrapper. Wrapping it would break clients that read
/// <c>build</c> directly off the root.
/// </remarks>
public sealed record ServerStatus(
    [property: JsonPropertyName("build")] BuildInfo Build,
    [property: JsonPropertyName("os")] OperatingSystemInfo Os);

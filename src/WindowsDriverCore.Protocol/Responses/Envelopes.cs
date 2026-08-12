using System.Collections.Generic;
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

/// <summary>A reference to an element, as W3C spells it.</summary>
/// <param name="Element">The element id, a dot-separated UIA RuntimeId.</param>
/// <remarks>
/// <b>The same id under a different key.</b> W3C names the property with a fixed
/// uuid so it cannot collide with an ordinary object member, and Selenium 4
/// reads exactly that. A client sent the JSON Wire spelling sees an object it
/// does not recognise as an element and fails on the NEXT command rather than
/// this one, which is the confusing way round.
/// </remarks>
public sealed record W3CElementReference(
    [property: JsonPropertyName("element-6066-11e4-a52e-4f735466cecf")] string Element);

/// <summary>The <c>value</c> of <c>GET /element/{id}/location</c>.</summary>
/// <param name="X">Left edge, <b>relative to the window</b>, not the screen.</param>
/// <param name="Y">Top edge, relative to the window.</param>
/// <remarks>
/// Measured: an element at screen <c>Left:257 Top:616</c> in a window at
/// <c>{54, 197}</c> reports <c>{x:203, y:419}</c>. Carries no size, which is why
/// it is a separate record from <see cref="ElementSize"/> rather than a
/// projection of one rectangle type.
/// </remarks>
public sealed record ElementLocation(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

/// <summary>The <c>value</c> of <c>GET /element/{id}/size</c>.</summary>
/// <param name="Height">Height.</param>
/// <param name="Width">Width.</param>
/// <remarks>
/// <b>Height before width</b>, which is the declaration order here and therefore
/// the serialisation order. Measured: WinAppDriver emits
/// <c>{"height":35,"width":97}</c>. Reversing these two lines changes the wire
/// format, so they are not alphabetical by accident.
/// </remarks>
public sealed record ElementSize(
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("width")] int Width);

/// <summary>An envelope whose <c>value</c> is worth translating between dialects.</summary>
/// <remarks>
/// <b>Non-generic on purpose.</b> The dialect filter sees responses as
/// <see cref="object"/> and cannot pattern-match <c>SessionResponse&lt;T&gt;</c>
/// without knowing every <c>T</c> — so it matches this instead. Nothing else
/// reads <see cref="Payload"/>; it exists so the translation has one seam rather
/// than a switch that grows a case per response type.
/// </remarks>
public interface IValueEnvelope : IJsonWireEnvelope
{
    /// <summary>The value about to be serialized, boxed.</summary>
    object? Payload { get; }
}

/// <summary>A response to a session-scoped command that returns a value.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="SessionId">The session the command ran against.</param>
/// <param name="Status">Always 0; a failure uses <see cref="FaultResponse"/>.</param>
/// <param name="Value">The result of the command.</param>
public sealed record SessionResponse<T>(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("value")] T Value) : IValueEnvelope
{
    /// <inheritdoc />
    [JsonIgnore]
    public object? Payload => Value;
}

/// <summary>The answer to a successful <c>POST /session</c>.</summary>
/// <param name="SessionId">The new session.</param>
/// <param name="Status">Always 0.</param>
/// <param name="Value">The capabilities echoed back.</param>
/// <remarks>
/// <b>Byte-identical to <see cref="SessionResponse{T}"/> on the wire</b> — same
/// three properties, same names, same order. It is a separate type only so the
/// dialect filter can recognise session creation without inspecting the request
/// path, which would have to account for the <c>/wd/hub</c> base path and would
/// silently stop matching if that ever moved.
/// </remarks>
public sealed record SessionCreatedResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("value")] IReadOnlyDictionary<string, string> Value)
    : IValueEnvelope
{
    /// <inheritdoc />
    [JsonIgnore]
    public object? Payload => Value;
}

/// <summary>A response to a session-scoped command that returns nothing.</summary>
/// <param name="SessionId">The session the command ran against.</param>
/// <param name="Status">Always 0.</param>
/// <remarks>Carries no <c>value</c> property at all, matching WinAppDriver.</remarks>
public sealed record VoidSessionResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] int Status) : IJsonWireEnvelope;

/// <summary>A response to a command not scoped to a session, such as <c>/sessions</c>.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Status">Always 0.</param>
/// <param name="Value">The result of the command.</param>
public sealed record ServerResponse<T>(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("value")] T Value) : IValueEnvelope
{
    /// <inheritdoc />
    [JsonIgnore]
    public object? Payload => Value;
}

/// <summary>A response carrying only a status, as <c>DELETE /session</c> does.</summary>
/// <param name="Status">Always 0.</param>
public sealed record VoidServerResponse(
    [property: JsonPropertyName("status")] int Status) : IJsonWireEnvelope;

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
    [property: JsonPropertyName("value")] FaultDetail Value) : IJsonWireEnvelope;

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

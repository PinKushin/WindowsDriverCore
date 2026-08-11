namespace WindowsDriverCore.Protocol.Responses;

/// <summary>
/// A response that carries a JSON Wire Protocol <c>status</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exists so one endpoint filter can read the status off any envelope without
/// knowing which of the five shapes it is holding. The alternative was for the
/// request log to buffer and re-parse the response body, which would have meant
/// paying for a 37 KB <c>/source</c> payload twice to learn a single integer.
/// </para>
/// <para>
/// <c>ServerStatus</c> deliberately does NOT implement this: <c>GET /status</c>
/// has no envelope and no status field, and giving it one here would put a
/// fabricated value on the wire eventually.
/// </para>
/// </remarks>
public interface IJsonWireEnvelope
{
    /// <summary>The JSON Wire status. <c>0</c> is success.</summary>
    int Status { get; }
}

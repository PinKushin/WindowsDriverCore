using System.Collections.Generic;

namespace WindowsDriverCore.Protocol.Sessions;

/// <summary>
/// One automation session: a client, an application, and the window it drives.
/// </summary>
/// <param name="Id">
/// The session id the client quotes on every subsequent request.
/// </param>
/// <param name="Capabilities">
/// The capabilities the session was created with, echoed back verbatim on
/// creation and in <c>GET /sessions</c>.
/// </param>
/// <param name="ProcessId">
/// The process the session drives, or 0 for a desktop (<c>Root</c>) session.
/// </param>
/// <param name="WindowHandle">
/// The top-level window the session is currently pointed at. Mutable over the
/// session's life because a client may switch windows.
/// </param>
public sealed record DriverSession(
    string Id,
    IReadOnlyDictionary<string, string> Capabilities,
    int ProcessId,
    nint WindowHandle)
{
    /// <summary>
    /// The window the session is pointed at, which <c>POST /session/:id/window</c>
    /// can change.
    /// </summary>
    /// <remarks>
    /// Deliberately the one mutable piece of session state. Everything else is
    /// fixed at creation, so a session cannot quietly become a different session.
    /// </remarks>
    public nint WindowHandle { get; set; } = WindowHandle;
}

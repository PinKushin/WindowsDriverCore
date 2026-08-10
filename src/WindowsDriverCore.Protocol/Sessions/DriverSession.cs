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
/// <param name="OwnsApplication">
/// Whether this driver started the application. Only then may it be ended when
/// the session is deleted: a desktop session addresses explorer, and an attached
/// session addresses a window somebody else opened.
/// </param>
public sealed record DriverSession(
    string Id,
    IReadOnlyDictionary<string, string> Capabilities,
    int ProcessId,
    nint WindowHandle,
    bool OwnsApplication = false)
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

    /// <summary>How long a find retries before reporting nothing found.</summary>
    /// <remarks>
    /// <para>
    /// <b>Zero by default, and that is this driver's choice rather than a copy.</b>
    /// WinAppDriver's default was not measured and is not asserted here. Zero
    /// follows from this project's own rule against hidden behaviour: a find that
    /// quietly retries makes a slow application look fast and a flaky one look
    /// reliable, and the caller never asked for it.
    /// </para>
    /// <para>
    /// Set by <c>POST /session/{id}/timeouts</c> with <c>type: implicit</c>. Per
    /// session, because the protocol scopes it there and two suites against one
    /// server must not affect each other.
    /// </para>
    /// </remarks>
    public TimeSpan ImplicitWait { get; set; } = TimeSpan.Zero;
}

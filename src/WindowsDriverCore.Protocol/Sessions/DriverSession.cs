using System.Collections.Generic;

using WindowsDriverCore.Platform.Windows;

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

    /// <summary>Whether typed input may still be in flight.</summary>
    /// <remarks>
    /// <para>
    /// <b>Set by typing, paid for by reading.</b> <c>SendInput</c> only queues
    /// keystrokes and the application consumes them on its own message loop, so a
    /// client that types and immediately reads can see the control mid-update.
    /// Measured 2026-08-11: 52 characters typed, and the client read <c>ab</c>.
    /// </para>
    /// <para>
    /// The flag exists so the wait costs nothing on the paths that do not need
    /// it. Typing stays at ~4 ms; a session that never types never waits; and a
    /// read pays the drain once per typing burst rather than on every request.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Set by every route that DISPATCHES synthesized input - keyboard, mouse,
    /// element actions - and cleared by the first read that depends on it.
    /// It was set by the keyboard route alone, so a read after a click never
    /// waited; typing and clicking are the same problem, since SendInput only
    /// queues and the application consumes on its own message loop.
    /// </remarks>
    public bool InputPending { get; set; }

    /// <summary>The modifier keys this session is holding between calls.</summary>
    /// <remarks>
    /// <b>Session state because the protocol says so.</b> <c>POST /keys</c>
    /// persists modifiers across calls — "Keys persist all modifier between API
    /// call and requires explicit modifier release" — so a shift opened by one
    /// request has to still be down when the next arrives. The element route is
    /// deliberately not part of this: it releases at the end of every call, which
    /// is the contract its own tests assert.
    /// </remarks>
    public HeldModifiers Modifiers { get; } = new();

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

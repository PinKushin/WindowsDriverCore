using System.Collections.Generic;
using System.Diagnostics;
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
/// <param name="Dialect">
/// Which protocol the client speaks, fixed when the session was created. Every
/// response this session produces is shaped for it, and the routes never learn
/// which one it is.
/// </param>
/// <param name="IsDesktop">
/// Whether this session addresses the whole desktop rather than one
/// application. A desktop session owns no application windows, and the suite
/// requires <c>window_handles</c> to answer EMPTY for it - not the desktop
/// window it happens to be rooted at.
/// </param>
public sealed record DriverSession(
    string Id,
    IReadOnlyDictionary<string, string> Capabilities,
    int ProcessId,
    nint WindowHandle,
    bool OwnsApplication = false,
    bool IsDesktop = false,
    ProtocolDialect Dialect = ProtocolDialect.JsonWire)
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

    /// <summary>Every window this session has opened, newest last.</summary>
    /// <remarks>
    /// <para>
    /// <b>A session addresses one window at a time but can own several.</b>
    /// <c>POST /appium/app/launch</c> relaunches the application in the SAME
    /// session, and <c>Launch_ModernApp</c> requires the handle count to go up
    /// by one and the current handle to change - so a single mutable
    /// <see cref="WindowHandle"/> cannot express what the protocol promises.
    /// </para>
    /// <para>
    /// Membership is not liveness: a window in this list may already be closed,
    /// which is why the read route filters by existence rather than trusting the
    /// list. Keeping a dead handle here is deliberate, because forgetting it
    /// would make a closed window indistinguishable from one this session never
    /// owned.
    /// </para>
    /// </remarks>
    private readonly List<nint> _windows = WindowHandle == 0 ? [] : [WindowHandle];

    /// <summary>Records a window this session has opened.</summary>
    /// <param name="handle">The new window.</param>
    public void AlsoOwns(nint handle)
    {
        lock (_windows)
        {
            if (handle != 0 && !_windows.Contains(handle))
            {
                _windows.Add(handle);
            }
        }
    }

    /// <summary>Every window handle this session owns, in the order opened.</summary>
    public IReadOnlyList<nint> OwnedWindows
    {
        get
        {
            lock (_windows)
            {
                return [.. _windows];
            }
        }
    }

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
    public bool InputPending
    {
        get => DispatchedAt is not null;
        set => DispatchedAt = value ? Stopwatch.GetTimestamp() : null;
    }

    /// <summary>When input was dispatched, or null when none is outstanding.</summary>
    /// <remarks>
    /// <para>
    /// <b>The TIME, not just the fact, because the fact is not enough to wait
    /// on.</b> <c>WaitForInputIdle</c> answers "is this process waiting for
    /// input" and returns in under a millisecond when the input has been
    /// injected but not yet delivered from the system queue - so a read
    /// immediately after a click sees the state before the click.
    /// </para>
    /// <para>
    /// MEASURED on the guest, per-test durations against the reference:
    /// </para>
    /// <code>
    ///                              WinAppDriver     this driver
    ///   MouseClick                       3.90 s         0.067 s   fails
    ///   ClickElement                     8.17 s         0.29 s    fails
    ///   GetElementDisplayedState         9.64 s         1.51 s    fails
    /// </code>
    /// <para>
    /// These tests carry no synchronisation of their own: they click and read.
    /// WinAppDriver passes because a find costs it ~1070 ms, so the application
    /// has caught up by accident. We fail by being 10-60x faster, which is a
    /// real defect and not a virtue - a driver that answers before the
    /// application has reacted has reported the wrong state.
    /// </para>
    /// </remarks>
    public long? DispatchedAt { get; private set; }

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

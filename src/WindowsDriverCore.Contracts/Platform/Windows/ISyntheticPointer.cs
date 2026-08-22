namespace WindowsDriverCore.Platform.Windows;

/// <summary>Where a frame sits in the life of one contact.</summary>
/// <remarks>
/// The model has three phases rather than a boolean because the hardware does.
/// A digitiser reports continuously while a contact is down, and a driver that
/// can only say "down" or "not down" cannot express a drag, a flick or a long
/// press — every one of which is a sequence of updates between a down and an up.
/// </remarks>
public enum SyntheticContactPhase
{
    /// <summary>The contact has just landed.</summary>
    Down,

    /// <summary>The contact is still down, possibly at a new position.</summary>
    Update,

    /// <summary>The contact has lifted.</summary>
    Up,
}

/// <summary>Which kind of pointer a synthetic contact represents.</summary>
public enum SyntheticPointerKind
{
    /// <summary>A finger.</summary>
    Touch,

    /// <summary>A pen, which additionally carries pressure and tilt.</summary>
    Pen,
}

/// <summary>
/// One contact in a synthetic pointer stream — a finger or a pen tip.
/// </summary>
/// <param name="Kind">Touch or pen.</param>
/// <param name="X">Screen x, in pixels.</param>
/// <param name="Y">Screen y, in pixels.</param>
/// <param name="Phase">
/// Where this frame sits in the contact's life. A real digitiser reports
/// <b>continuously</b> while a finger is down — down, then update after update,
/// then up — and a bare down/up pair with nothing between is not what the system
/// expects to see.
/// </param>
/// <param name="Pressure">
/// Pen pressure, 0 to 1. Ignored for touch. W3C's default is 0.5 and the
/// suite validates the range, so it is carried rather than assumed.
/// </param>
/// <param name="TiltX">Pen tilt left/right, -90 to 90. Ignored for touch.</param>
/// <param name="TiltY">Pen tilt up/down, -90 to 90. Ignored for touch.</param>
public readonly record struct SyntheticContact(
    SyntheticPointerKind Kind,
    int X,
    int Y,
    SyntheticContactPhase Phase,
    double Pressure = 0.5,
    int TiltX = 0,
    int TiltY = 0);

/// <summary>
/// Injects pen and touch input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <c>IPointerInput</c> because the mechanism is separate.</b>
/// Mouse input goes through <c>SendInput</c>, which has no concept of a contact
/// area, pressure or tilt, and which reports itself to applications as a mouse.
/// A test that asks for a touch tap and receives a mouse click has been told the
/// wrong thing happened — the same class of lie as reporting a session on a dead
/// window.
/// </para>
/// <para>
/// <b>The compatibility floor is a real constraint here.</b> Touch injection
/// (<c>InitializeTouchInjection</c>) is Windows 8 and later, so it is inside the
/// floor of Windows 10 1607. Pen injection
/// (<c>CreateSyntheticPointerDevice</c>) arrived in Windows 10 <b>1809</b>,
/// which is ABOVE the floor — so pen is unavailable on the oldest supported
/// systems and must say so rather than silently behaving like touch.
/// </para>
/// <para>
/// Measured 2026-08-11: 25 Touch and Pen tests in WinAppDriver's own suite pass
/// against WinAppDriver and fail here, which is what this exists to close.
/// </para>
/// <para>
/// <b>What the application under test sees is its own choice, and Windows
/// supplies the fallback.</b> Injection produces real <c>WM_POINTER</c> input. A
/// UWP or WinUI application consumes that natively and observes touch. A WPF
/// application does not, by default, because WPF takes touch through the WISP
/// stylus stack — so Windows PROMOTES the contact to a mouse event and the
/// interaction still lands. A caller asking for a tap against a plain WPF button
/// gets the button pressed, which is why no fallback is needed here.
/// </para>
/// <para>
/// The boundary is multi-touch: promotion is per contact, so a one-finger gesture
/// degrades to a mouse and a two-finger one has no mouse equivalent and simply
/// does nothing. Nothing wrong happens — which is the better of the two failure
/// modes, and worth preferring to inventing a substitute gesture.
/// </para>
/// </remarks>
public interface ISyntheticPointer
{
    /// <summary>Whether this kind of pointer can be injected on this system.</summary>
    /// <param name="kind">Touch or pen.</param>
    /// <returns>
    /// False when the platform lacks the API — pen below Windows 10 1809. A
    /// caller must be able to report "this system cannot" rather than have a
    /// silent no-op counted as success.
    /// </returns>
    bool CanInject(SyntheticPointerKind kind);

    /// <summary>Injects one frame of contact state.</summary>
    /// <param name="contacts">
    /// Every contact in the frame. Multi-touch is expressed by passing more than
    /// one; a gesture is a sequence of frames.
    /// </param>
    /// <returns>True if the system accepted the frame.</returns>
    bool Inject(IReadOnlyList<SyntheticContact> contacts);

    /// <summary>
    /// The Win32 error from the most recent failed <see cref="Inject"/>, or 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added because four guest measurements produced a boolean and no
    /// reason.</b> A touch lift following a long move is refused, the lift after
    /// a short move is not, and every frame in between succeeds — a contradiction
    /// that cannot be resolved from a true/false. The P/Invokes already declare
    /// <c>SetLastError</c>; nothing ever read it.
    /// </para>
    /// <para>
    /// <b>Read it only immediately after a false return.</b> It is not cleared on
    /// success, so a stale value from an earlier failure is still there — which is
    /// fine for the one use it has, naming the reason in the refusal that is being
    /// built right then, and wrong for anything else.
    /// </para>
    /// </remarks>
    int LastInjectionError { get; }
}

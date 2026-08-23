using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Performs a validated W3C pointer action sequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>W3C first, deliberately.</b> <c>/actions</c> is where W3C put every input
/// primitive, so the JSON Wire <c>/touch/*</c> and mouse routes become thin
/// wrappers over this rather than a second implementation. Two input paths that
/// must be kept in step is exactly how WinAppDriver's own XPath singular and
/// plural handling drifted apart into issue #1079.
/// </para>
/// <para>
/// <b>Validation is not repeated here.</b> <see cref="ActionRoutes"/> has already
/// refused a malformed payload with the suite's own message; anything reaching
/// this has a shape worth executing. Re-checking would put the same rule in two
/// places and let them disagree.
/// </para>
/// <para>
/// <b>A pause is honoured by not injecting, not by sleeping.</b> W3C's
/// <c>duration</c> describes how long a move should take, and a driver that slept
/// through it would block the request thread for the caller's benefit and
/// nobody else's. Frames are emitted along the path instead; the tick that a real
/// digitiser supplies is the thing being imitated, not the wall clock.
/// </para>
/// </remarks>
public sealed class PointerActionRunner
{
    private readonly ISyntheticPointer _synthetic;
    private readonly IElementInspector _elements;
    private readonly IWindowLocator _windows;

    /// <summary>How long a drag with no stated duration takes.</summary>
    /// <remarks>
    /// The touch routes carry no duration of their own, but a gesture delivered
    /// instantly is not one the window manager can follow - measured against the
    /// reference, which spends 1261 ms on a 1000 ms move and repositions the
    /// window, where this driver spent 50 ms and moved nothing.
    /// </remarks>
    private static readonly TimeSpan DragDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The least separation that makes injected frames distinct input events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a SEPARATION, not a drag duration, and the distinction is the
    /// whole point.</b> In <c>/actions</c> the client states how long a move
    /// takes and this driver spends exactly that - the caller asked. The
    /// multi-request <c>/touch/*</c> trio carries no duration at all, and the
    /// caller has ALREADY expressed the drag's length by how far apart it sent
    /// the three requests. A duration invented here would be added on top of
    /// one the client never asked for.
    /// </para>
    /// <para>
    /// <b>MEASURED, single <c>/touch/move</c>, window actually moving:</b>
    /// </para>
    /// <code>
    ///    0 ms  no      25 ms  YES
    ///   10 ms  no     300 ms  YES
    /// </code>
    /// <para>
    /// The cliff sits between 10 and 25 ms across ten frames — roughly 2 ms of
    /// separation per frame. Below it the per-frame remainder rounds to nothing,
    /// no sleep happens, and the frames arrive as one burst the system coalesces
    /// into a single jump. So what matters is that the frames are separate
    /// events, not that the gesture is slow.
    /// </para>
    /// <para>
    /// <b>Why not 300 ms.</b> That value was inherited from the <c>/actions</c>
    /// path and defended by noting WinAppDriver spends ~100 ms per touch phase —
    /// a measurement taken while timing was still believed to be the cause, and
    /// worthless as justification once the cause turned out to be the injection
    /// API. It is twelve times more than needed, and this driver's whole argument
    /// is speed: a find costs ~33 ms here against ~1070 ms through WinAppDriver,
    /// so handing back 300 ms of self-imposed wait per move is the wrong trade.
    /// </para>
    /// <para>
    /// 5 ms per frame is roughly double the measured threshold — margin for a
    /// loaded machine's scheduling without pretending the exact figure
    /// generalises off the one desktop it was measured on.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan FrameSeparation =
        TimeSpan.FromMilliseconds(5 * FramesPerMove);

    /// <summary>How many frames a move is broken into.</summary>
    /// <remarks>
    /// A move reported as a single jump is not a gesture — a flick and a drag are
    /// distinguished by the path between the endpoints, and an application
    /// watching for velocity sees none from one frame. Ten is enough for a target
    /// to see motion without flooding the queue.
    /// </remarks>
    private const int FramesPerMove = 10;

    /// <summary>Creates the runner.</summary>
    /// <param name="synthetic">Injects pen and touch.</param>
    /// <param name="elements">Resolves an element origin to a screen point.</param>
    /// <param name="windows">
    /// Places the session window, and answers whether a point belongs to it.
    /// Required rather than optional: an absent guard cannot refuse, so a null
    /// collaborator would make "refuses a point outside the window" and "has no
    /// guard at all" predict the same observation.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// No mouse collaborator, deliberately: <see cref="ActionRoutes"/> refuses any
    /// pointer type but pen and touch, with the suite's own message. Accepting one
    /// here would be dead code that looks like a supported path.
    /// </remarks>
    public PointerActionRunner(
        ISyntheticPointer synthetic, IElementInspector elements, IWindowLocator windows)
    {
        ArgumentNullException.ThrowIfNull(synthetic);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(windows);

        _synthetic = synthetic;
        _elements = elements;
        _windows = windows;
    }

    /// <summary>Performs every pointer source in the payload.</summary>
    /// <param name="payload">A payload that has already been validated.</param>
    /// <param name="window">The session's window, for element origins.</param>
    /// <returns>
    /// Null when everything was performed, or why it could not be. A refusal is
    /// reported rather than swallowed: a caller told an action succeeded when
    /// nothing moved is the failure this driver exists to fix.
    /// </returns>
    public PointerRefusal? Perform(JsonElement payload, nint window)
    {
        if (!payload.TryGetProperty("actions", out JsonElement sources))
        {
            return null;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (Text(source, "type") != "pointer")
            {
                // key and none sources are someone else's job. Skipped rather
                // than refused, because a mixed payload's pointer half is still
                // worth performing.
                continue;
            }

            PointerRefusal? failure = PerformSource(source, window);
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private PointerRefusal? PerformSource(JsonElement source, nint window)
    {
        SyntheticPointerKind kind = PointerKind(source);

        if (!_synthetic.CanInject(kind))
        {
            // Said plainly rather than performed as something else. Pen needs
            // Windows 10 1809 and the floor is 1607, so a supported system can
            // genuinely lack it - and silently substituting touch would report
            // that a pen gesture happened when none did.
            return PointerRefusal.Reason(
                $"This system cannot inject {kind.ToString().ToLowerInvariant()} input");
        }

        if (!source.TryGetProperty("actions", out JsonElement steps))
        {
            return null;
        }

        // Where the contact is now. W3C treats a pointer as having a position
        // between actions, so a pointerDown with no preceding move happens
        // wherever the previous action left it.
        int x = 0;
        int y = 0;
        bool down = false;

        foreach (JsonElement step in steps.EnumerateArray())
        {
            string? type = Text(step, "type");

            switch (type)
            {
                case "pointerMove":
                {
                    (int toX, int toY, PointerRefusal? failure) = Target(step, window, x, y);
                    if (failure is not null)
                    {
                        return failure;
                    }

                    PointerRefusal? moved = Move(kind, x, y, toX, toY, down, DurationOf(step));
                    if (moved is not null)
                    {
                        return moved;
                    }

                    x = toX;
                    y = toY;
                    break;
                }

                case "pointerDown":
                {
                    PointerRefusal? pressed = Press(kind, window, x, y, step);
                    if (pressed is not null)
                    {
                        return pressed;
                    }

                    down = true;
                    break;
                }

                case "pointerUp":
                {
                    PointerRefusal? released = Release(kind, x, y, step);
                    if (released is not null)
                    {
                        return released;
                    }

                    down = false;
                    break;
                }

                default:
                    // pause, and anything else the validator allowed. A pause
                    // between injected frames needs no action: the frames it
                    // separates are already discrete events.
                    break;
            }
        }

        return null;
    }
    /// <summary>
    /// Puts a contact down, moves it, or lifts it — one phase per call.
    /// </summary>
    /// <param name="window">The session window, which the point must own.</param>
    /// <param name="windowX">X, RELATIVE TO THE WINDOW.</param>
    /// <param name="windowY">Y, relative to the window.</param>
    /// <param name="phase">Which half of the gesture this is.</param>
    /// <returns>The refusal, or null when the contact was injected.</returns>
    /// <remarks>
    /// <para>
    /// <b>The contact survives BETWEEN requests, which is what makes this
    /// different from <see cref="Tap"/>.</b> <c>touch/down</c> and
    /// <c>touch/up</c> are separate HTTP calls, so the injection device has to
    /// outlive either one — it is a DI singleton for exactly that reason. A
    /// per-request device would lift the contact the moment the down request
    /// returned and the up would arrive with nothing held.
    /// </para>
    /// <para>
    /// <b>Window-relative in, screen out.</b> The suite computes these from
    /// <c>element.Location</c>, which this driver answers window-relative, so
    /// treating them as screen pixels would put every contact at that offset
    /// from the DESKTOP origin — up and to the left of the window, into whatever
    /// happens to be there.
    /// </para>
    /// <para>
    /// <b>The ownership guard is on DOWN only</b>, matching the existing pointer
    /// path: a move follows a press that was already checked, and refusing each
    /// frame would turn a drag that crosses an edge into a failure mid-gesture.
    /// </para>
    /// </remarks>
    public PointerRefusal? Contact(
        nint window, int windowX, int windowY, SyntheticContactPhase phase)
    {
        if (!_synthetic.CanInject(SyntheticPointerKind.Touch))
        {
            return PointerRefusal.Reason("This system cannot inject touch input");
        }

        (int x, int y, PointerRefusal? placement) = WindowOrigin(window, windowX, windowY);
        if (placement is not null)
        {
            return placement;
        }

        if (phase == SyntheticContactPhase.Down && Refuse(window, x, y) is { } refusal)
        {
            return refusal;
        }

        // AN UPDATE IS INTERPOLATED FROM WHERE THE CONTACT ACTUALLY IS, exactly
        // as a move inside /actions is - and for the same measured reason. A
        // window manager samples the pointer across its own message loop, so a
        // single frame teleporting 100 px is not a gesture it can follow.
        //
        // The /actions drag was fixed this way and Touch_DragAndDrop and
        // Pen_DragAndDrop passed. This path - the multi-request
        // /touch/down, /touch/move, /touch/up trio - still injected one frame
        // per request, and TouchDownMoveUp_DragAndDrop and MouseDownMoveUp both
        // failed with the window never moving: "Expected any value except
        // {X=290,Y=154}. Actual: {X=290,Y=154}".
        //
        // WinAppDriver gets this for free by being slow: three separate HTTP
        // round trips give the window manager time and intermediate samples that
        // this driver, answering in single-digit milliseconds, does not.
        if (phase == SyntheticContactPhase.Update &&
            _contacts.TryGetValue(window, out (int X, int Y, int Thread) from))
        {
            // THE PATH IS WALKED, BUT NO WALL CLOCK IS SPENT, and the difference
            // between this and the /actions path is the whole reason both exist.
            //
            // In /actions the duration is SEMANTIC: down, move and up all arrive
            // in ONE request, so without pacing the entire gesture completes in
            // microseconds and the window manager never samples it. The client
            // said "this move takes a second" and meant it.
            //
            // Here down, move and up are THREE separate HTTP requests, and the
            // client stated no duration at all - so the pacing is this driver's
            // choice rather than the caller's instruction.
            //
            // THIS COMMENT USED TO ARGUE THE OPPOSITE and was wrong twice over.
            // It said pacing "invents a delay the reference never spends", and
            // it cited a measurement where pacing broke the lift.
            //
            // The reference DOES spend it. Measured in one session on the guest:
            //
            //   GET  /status        37.5 ms    <- baseline, injects nothing
            //   POST /timeouts      73.3 ms
            //   POST /touch/move   153.4 ms    <- ~100 ms above its own baseline
            //
            // And the broken lift was never the pacing. It was the Win32
            // injector having no session to hold a contact between requests;
            // with the WinRT injector in place the same pacing lifts cleanly and
            // the window actually moves. See WinRtTouchInjector.
            //
            // Without the pacing the window manager gets the whole path in a
            // single burst and does not register a drag - measured here, MOVED:
            // NO at TimeSpan.Zero and MOVED: YES at 25 ms, everything else held
            // constant. See FrameSeparation for the sweep and for why this is a
            // separation rather than a duration.
            PointerRefusal? moved = Move(
                SyntheticPointerKind.Touch, from.X, from.Y, x, y, down: true, FrameSeparation);

            if (moved is not null)
            {
                return moved;
            }

            _contacts[window] = (x, y, from.Thread);
            return null;
        }

        if (!_synthetic.Inject([Plain(x, y, phase)]))
        {
            return PointerRefusal.Reason(
                $"The system refused a touch contact ({phase})" +
                $"{Because(_synthetic.LastInjectionError)}{AcrossThreads(window)}");
        }

        // Tracked so the NEXT update knows where it is starting from. Removed on
        // lift, because a move after an up is a hover and has no path to walk -
        // and because leaving it would make this dictionary grow for the
        // server's lifetime.
        if (phase == SyntheticContactPhase.Up)
        {
            _contacts.TryRemove(window, out _);
        }
        else
        {
            // THE THREAD THAT OPENED THE CONTACT IS RECORDED WITH IT.
            //
            // HYPOTHESIS UNDER TEST: InjectTouchInput's contact state may be
            // per-thread. down, move and up are three separate HTTP requests and
            // ASP.NET may serve them on different thread-pool threads, so a
            // contact opened on one and lifted on another would be genuinely
            // invalid - which is exactly the ERROR_INVALID_PARAMETER measured at
            // 982eb32 - and the duration correlation would follow without the OS
            // dropping anything, because a fast gesture is likelier to reuse the
            // same pooled thread.
            //
            // Recorded rather than acted on: this makes the refusal name both
            // threads, so the next failure says whether they differ instead of
            // leaving it to be argued.
            _contacts[window] = (x, y, Environment.CurrentManagedThreadId);
        }

        return null;
    }

    /// <summary>
    /// Where each window's multi-request touch contact currently is, in screen
    /// pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>State on a singleton, deliberately and narrowly.</b> A gesture built
    /// from <c>/touch/down</c>, <c>/touch/move</c> and <c>/touch/up</c> spans
    /// three HTTP requests, so the path between two points is not derivable from
    /// either request alone — the driver has to remember where the finger was.
    /// The <c>/actions</c> route needs none of this because a whole sequence
    /// arrives in one payload.
    /// </para>
    /// <para>
    /// <b>Keyed by WINDOW rather than held as a single field, because this type
    /// is a singleton shared by every session.</b> One field would let two
    /// sessions dragging at once interpolate from each other's finger — a bug
    /// that would only appear under concurrent use and would be very hard to
    /// attribute. A contact belongs to the window it was pressed on.
    /// </para>
    /// <para>
    /// <b>An absent entry means no contact is down</b>, which is also the state
    /// after a lift. An update with no entry falls through to a single frame
    /// rather than interpolating from a stale point — a finger that was never
    /// pressed has no path.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<nint, (int X, int Y, int Thread)> _contacts = new();

    /// <summary>Forgets every tracked contact, unconditionally.</summary>
    /// <remarks>
    /// <para>
    /// A test seam, not a production operation — the same reason and the same
    /// shape as <see cref="Sessions.ISessionStore.Clear"/> and
    /// <see cref="Sessions.IElementRegistry.Clear"/>. This type is a singleton,
    /// so a <c>/touch/down</c> in one test is still down for the next one, and a
    /// fixture that shares a server needs to be able to say "no finger is on the
    /// screen" between tests.
    /// </para>
    /// <para>
    /// <b>Production does not need it, and that is worth stating rather than
    /// assuming.</b> An entry survives only a gesture that pressed and never
    /// lifted, is keyed by window so it cannot affect a different one, and is
    /// overwritten by that window's next <c>down</c>. The dictionary is bounded
    /// by the number of windows with a contact outstanding, which is normally
    /// zero.
    /// </para>
    /// </remarks>
    public void ForgetContacts() => _contacts.Clear();


    /// <summary>Taps a point, holding the contact for a duration.</summary>
    /// <param name="x">Screen x.</param>
    /// <param name="y">Screen y.</param>
    /// <param name="hold">How long to keep the contact down.</param>
    /// <returns>Null when performed, or why it could not be.</returns>
    /// <remarks>
    /// <b>The hold really does pass time, and that is the operation rather than a
    /// wait.</b> This project bans sleeping as a substitute for synchronising on a
    /// condition — waiting and hoping. A long press is different in kind: the
    /// caller asked for a contact held for a duration, and an application
    /// distinguishes a tap from a long press by exactly that. Returning early
    /// would perform a different gesture from the one requested.
    /// </remarks>
    public PointerRefusal? Tap(int x, int y, TimeSpan hold)
    {
        if (!_synthetic.CanInject(SyntheticPointerKind.Touch))
        {
            return PointerRefusal.Reason("This system cannot inject touch input");
        }

        if (!_synthetic.Inject([Plain(x, y, SyntheticContactPhase.Down)]))
        {
            return PointerRefusal.Reason("The system refused a touch contact");
        }

        // Updates while held. A contact that is down and silent is not what a
        // digitiser produces, and a target watching for a long press needs to
        // keep seeing it.
        long deadline = Environment.TickCount64 + (long)hold.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!_synthetic.Inject([Plain(x, y, SyntheticContactPhase.Update)]))
            {
                return PointerRefusal.Reason(
                    $"The system refused a contact update{Because(_synthetic.LastInjectionError)}");
            }

            Thread.Sleep(HoldFrameMilliseconds);
        }

        return _synthetic.Inject([Plain(x, y, SyntheticContactPhase.Up)])
            ? null
            : PointerRefusal.Reason("The system refused to lift a touch contact");
    }

    /// <summary>Drags from one point to another with the contact down.</summary>
    /// <param name="fromX">Screen x to start at.</param>
    /// <param name="fromY">Screen y to start at.</param>
    /// <param name="toX">Screen x to finish at.</param>
    /// <param name="toY">Screen y to finish at.</param>
    /// <returns>Null when performed, or why it could not be.</returns>
    public PointerRefusal? Drag(int fromX, int fromY, int toX, int toY)
    {
        if (!_synthetic.CanInject(SyntheticPointerKind.Touch))
        {
            return PointerRefusal.Reason("This system cannot inject touch input");
        }

        if (!_synthetic.Inject([Plain(fromX, fromY, SyntheticContactPhase.Down)]))
        {
            return PointerRefusal.Reason("The system refused a touch contact");
        }

        // A DURATION HERE TOO. This is the same gesture the W3C path performs,
        // and the same window-manager sampling applies - a drag delivered
        // instantly moves nothing. The caller supplies no duration on this
        // route, so it gets the one a person's flick takes.
        PointerRefusal? moved = Move(
            SyntheticPointerKind.Touch, fromX, fromY, toX, toY, down: true, DragDuration);
        if (moved is not null)
        {
            return moved;
        }

        return _synthetic.Inject([Plain(toX, toY, SyntheticContactPhase.Up)])
            ? null
            : PointerRefusal.Reason("The system refused to lift a touch contact");
    }

    /// <summary>The centre of an element, in screen pixels.</summary>
    /// <param name="window">The session's window.</param>
    /// <param name="elementId">The element.</param>
    /// <returns>The point, or why it could not be found.</returns>
    public (int X, int Y, PointerRefusal? Failure) CentreOf(nint window, string elementId)
    {
        ElementRead<ElementBounds> bounds = _elements.ScreenBounds(window, elementId);

        return bounds.Outcome == ElementReadOutcome.Read
            ? (bounds.Value.X + (bounds.Value.Width / 2),
               bounds.Value.Y + (bounds.Value.Height / 2),
               null)
            : (0, 0, PointerRefusal.Element(bounds.Outcome, elementId));
    }

    /// <summary>Turns a window-relative point into a screen point.</summary>
    /// <remarks>
    /// A window that cannot be placed is refused rather than treated as being at
    /// the desktop origin. Falling back to <c>(0,0)</c> would silently convert a
    /// missing window into a gesture on whatever occupies the top-left corner of
    /// the screen, which is the failure this whole path is being corrected for.
    /// </remarks>
    private (int X, int Y, PointerRefusal? Failure) WindowOrigin(nint window, int dx, int dy)
    {
        WindowBounds? bounds = _windows.GetBounds(window);

        return bounds is null
            ? (0, 0, PointerRefusal.Reason(
                "The session window could not be placed, so a viewport coordinate has no meaning"))
            : (bounds.X + dx, bounds.Y + dy, null);
    }

    /// <summary>
    /// Refuses a point the session window does not own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The guard this path never had.</b> <c>UiaElementInteractor</c> has asked
    /// <see cref="IWindowLocator.OwnsThePointAt"/> before every mouse click since
    /// the click ladder was written — "an unguarded coordinate click is worse than
    /// no click" — and the pointer path was dispatching synthesized contacts at
    /// arbitrary coordinates with no such question asked.
    /// </para>
    /// <para>
    /// <b>Ownership, not containment.</b> A covered window still contains the
    /// point and is not what a contact there reaches. That distinction is the
    /// whole reason the locator answers this rather than a rectangle test.
    /// </para>
    /// </remarks>
    private PointerRefusal? Refuse(nint window, int x, int y) =>
        _windows.OwnsThePointAt(x, y, window)
            ? null
            : PointerRefusal.Reason(
                $"({x},{y}) is outside the application window, so the input was not dispatched");

    /// <summary>Names the threads a gesture spanned, when it spanned more than one.</summary>
    /// <param name="window">The window whose contact is being lifted.</param>
    /// <returns>A trailing clause, or empty when there is nothing to say.</returns>
    /// <remarks>
    /// <b>Reports only the difference, not the identity.</b> A thread id on its
    /// own is noise in a transcript; two that disagree at the moment an
    /// injection is refused is the whole hypothesis. Empty when no contact is
    /// tracked or when the gesture stayed on one thread, so this adds nothing to
    /// the ordinary case.
    /// </remarks>
    private string AcrossThreads(nint window) =>
        _contacts.TryGetValue(window, out (int X, int Y, int Thread) contact) &&
        contact.Thread != Environment.CurrentManagedThreadId
            ? $" [contact opened on thread {contact.Thread}, lifting on {Environment.CurrentManagedThreadId}]"
            : string.Empty;

    /// <summary>Names the Win32 reason an injection was refused.</summary>
    /// <param name="error">The captured error, or 0 when there is none.</param>
    /// <returns>A trailing clause, or empty when nothing is known.</returns>
    /// <remarks>
    /// <b>Four guest measurements produced a boolean and no reason.</b> A touch
    /// lift after a long move is refused while the lift after a short move is
    /// not, and every frame in between succeeds - which is a contradiction that
    /// a true/false cannot resolve. The two codes named here are the ones worth
    /// recognising on sight: ERROR_TIMEOUT means the contact was dropped for
    /// going unrefreshed, and ERROR_INVALID_PARAMETER means the frame itself was
    /// rejected. Anything else is reported as a number rather than guessed at.
    /// </remarks>
    private static string Because(int error) => error switch
    {
        0 => string.Empty,
        87 => " (ERROR_INVALID_PARAMETER - the frame was rejected)",
        1121 => " (ERROR_TIMEOUT - the contact was dropped before this frame)",
        _ => $" (Win32 error {error})",
    };

    private static SyntheticContact Plain(int x, int y, SyntheticContactPhase phase) =>
        new(SyntheticPointerKind.Touch, x, y, phase);

    /// <summary>How often a held contact reports while it is held.</summary>
    /// <remarks>
    /// Roughly a 60 Hz digitiser. Faster floods the queue for no benefit; slower
    /// and a target sampling for a long press can miss it.
    /// </remarks>
    private const int HoldFrameMilliseconds = 16;

    /// <summary>Puts a contact down, if the window owns the point.</summary>
    /// <remarks>
    /// <b>The guard is on DOWN and not on every frame, deliberately.</b> A move
    /// while the contact is up injects nothing, and a move while it is down
    /// follows a press that was already checked — so this is the one place where
    /// asking changes what reaches the desktop. Checking each interpolated frame
    /// would also refuse a drag the moment it crossed an edge, turning a partial
    /// gesture into a failure the caller cannot act on.
    /// </remarks>
    private PointerRefusal? Press(
        SyntheticPointerKind kind, nint window, int x, int y, JsonElement step)
    {
        PointerRefusal? outside = Refuse(window, x, y);
        if (outside is not null)
        {
            return outside;
        }

        return _synthetic.Inject([Contact(kind, x, y, SyntheticContactPhase.Down, step)])
            ? null
            : PointerRefusal.Reason(
                $"The system refused a {kind.ToString().ToLowerInvariant()} contact");
    }

    private PointerRefusal? Release(SyntheticPointerKind kind, int x, int y, JsonElement step) =>
        _synthetic.Inject([Contact(kind, x, y, SyntheticContactPhase.Up, step)])
            ? null
            : PointerRefusal.Reason(
                $"The system refused to lift a {kind.ToString().ToLowerInvariant()} contact");

    private PointerRefusal? Move(
        SyntheticPointerKind kind, int fromX, int fromY, int toX, int toY, bool down, TimeSpan duration)
    {
        if (!down)
        {
            // Not in contact, so there is nothing to report. A hovering finger
            // does not exist; a hovering pen does, but reporting it needs
            // INRANGE without INCONTACT and no suite test asks for it yet.
            return null;
        }

        // THE DURATION IS SPENT, not skipped, and that reverses an earlier
        // decision in this file. Frames used to be emitted as fast as they could
        // be, reasoning that sleeping blocks the request thread for the caller's
        // benefit alone. MEASURED on the guest, dragging Calculator's title bar
        // with a 1000 ms move:
        //
        //   WinAppDriver   POST /actions 1261 ms -> window moved 207,64 to 297,154
        //   this driver    POST /actions   50 ms -> window did not move at all
        //
        // A window manager samples the pointer across its own message loop, so a
        // hundred frames delivered in a microsecond is not a gesture it can
        // follow. The duration is SEMANTIC here rather than a timeout: the
        // client asked for a move lasting a second because that is what the
        // application needs to see.
        long frameTicks = duration > TimeSpan.Zero
            ? duration.Ticks / FramesPerMove
            : 0;

        // Interpolated, because a drag is the PATH and not the endpoints. An
        // application measuring velocity or distinguishing a flick from a press
        // sees nothing in a single jump.
        for (int frame = 1; frame <= FramesPerMove; frame++)
        {
            long frameDue = Stopwatch.GetTimestamp();

            int stepX = fromX + (((toX - fromX) * frame) / FramesPerMove);
            int stepY = fromY + (((toY - fromY) * frame) / FramesPerMove);

            if (!_synthetic.Inject(
                [new SyntheticContact(kind, stepX, stepY, SyntheticContactPhase.Update)]))
            {
                return PointerRefusal.Reason(
                    $"The system refused a contact update{Because(_synthetic.LastInjectionError)}");
            }

            if (frameTicks > 0)
            {
                // Paced against the clock rather than slept blindly, so the
                // cost of injecting a frame comes out of the interval instead of
                // being added to it. A move asking for a second takes about a
                // second, not a second plus a hundred injections.
                TimeSpan spent = Stopwatch.GetElapsedTime(frameDue);
                TimeSpan remaining = TimeSpan.FromTicks(frameTicks) - spent;

                if (remaining > TimeSpan.Zero)
                {
                    Thread.Sleep(remaining);
                }
            }
        }

        return null;
    }

    /// <summary>How long a step asked to take.</summary>
    /// <remarks>
    /// Absent means instant, which is what W3C says and what a pointerDown or a
    /// move with no duration wants. Validation has already rejected a negative
    /// or non-numeric value, so anything arriving here is usable.
    /// </remarks>
    private static TimeSpan DurationOf(JsonElement step) =>
        step.TryGetProperty("duration", out JsonElement duration) &&
        duration.ValueKind == JsonValueKind.Number &&
        duration.TryGetDouble(out double milliseconds) &&
        milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.Zero;

    private (int X, int Y, PointerRefusal? Failure) Target(
        JsonElement step, nint window, int currentX, int currentY)
    {
        int dx = step.TryGetProperty("x", out JsonElement xValue) && xValue.TryGetInt32(out int x) ? x : 0;
        int dy = step.TryGetProperty("y", out JsonElement yValue) && yValue.TryGetInt32(out int y) ? y : 0;

        if (!step.TryGetProperty("origin", out JsonElement origin))
        {
            return (dx, dy, null);
        }

        if (origin.ValueKind == JsonValueKind.String)
        {
            return origin.GetString() switch
            {
                // Relative to where the pointer already is.
                "pointer" => (currentX + dx, currentY + dy, null),

                // VIEWPORT IS THE WINDOW, NOT THE SCREEN. This read "a desktop
                // session's viewport IS the screen" and that was wrong in a way
                // that escaped the application under test.
                //
                // The suite's own comment settles it — Touch_Click_OriginViewport
                // feeds element.Location straight into a viewport move and calls
                // it "relative to application window" — and this driver's
                // /location already answers window-relative, measured against
                // WinAppDriver and recorded in ElementPropertyRoutes. Treating
                // the same numbers as screen pixels put every viewport gesture at
                // that offset from the DESKTOP origin: up and to the left of the
                // window, onto whatever happens to be there. Injected input that
                // lands outside the application under test is the worst failure
                // this driver has, because the damage is somebody else's.
                _ => WindowOrigin(window, dx, dy),
            };
        }

        // An element origin. The offset is from the element's CENTRE, which is
        // W3C's rule and not its top-left - getting that wrong puts every
        // element-relative gesture half a control away from where it belongs.
        //
        // The KIND is checked before the keys are read. JsonElement.TryGetProperty
        // THROWS on anything that is not an object, so a payload sending
        // "origin": null - which the suite's ActionsError_NullElement does
        // verbatim - took the whole request down as an unhandled exception and
        // answered a 500 HTML page instead of a fault a client can read.
        string? elementId = origin.ValueKind == JsonValueKind.Object
            ? ElementId(origin)
            : null;
        if (elementId is null)
        {
            // The BAD-ORIGIN sentence, not a private one. An origin that is
            // null, or an object with no element key, is a malformed argument
            // rather than a missing element - and the suite's ActionsNullElement
            // expectation is a SUFFIX of its bad-origin message, so this one
            // rejection satisfies both ActionsError_NullElement and
            // ActionsError_BadPointerOrigin. The sentence this used to send
            // matched neither.
            return (0, 0, PointerRefusal.Reason(ElementFault.BadOriginMessage));
        }

        ElementRead<ElementBounds> bounds = _elements.ScreenBounds(window, elementId);
        if (bounds.Outcome != ElementReadOutcome.Read)
        {
            return (0, 0, PointerRefusal.Element(bounds.Outcome, elementId));
        }

        return (
            bounds.Value.X + (bounds.Value.Width / 2) + dx,
            bounds.Value.Y + (bounds.Value.Height / 2) + dy,
            null);
    }

    private static SyntheticContact Contact(
        SyntheticPointerKind kind, int x, int y, SyntheticContactPhase phase, JsonElement step) =>
        new(
            kind,
            x,
            y,
            phase,
            Number(step, "pressure", 0.5),
            (int)Number(step, "tiltX", 0),
            (int)Number(step, "tiltY", 0));

    private static SyntheticPointerKind PointerKind(JsonElement source)
    {
        string? declared = source.TryGetProperty("parameters", out JsonElement parameters)
            ? Text(parameters, "pointerType")
            : null;

        return declared switch
        {
            "touch" => SyntheticPointerKind.Touch,
            "pen" => SyntheticPointerKind.Pen,
            _ => SyntheticPointerKind.Touch,
        };
    }

    private static string? ElementId(JsonElement origin)
    {
        // Both spellings. JSON Wire says ELEMENT and W3C says the long uuid key,
        // and a client may send either - accepting both costs nothing and
        // refusing one would break exactly the clients this driver exists for.
        if (origin.TryGetProperty("ELEMENT", out JsonElement jwp) &&
            jwp.ValueKind == JsonValueKind.String)
        {
            return jwp.GetString();
        }

        return origin.TryGetProperty(
                "element-6066-11e4-a52e-4f735466cecf", out JsonElement w3c) &&
            w3c.ValueKind == JsonValueKind.String
            ? w3c.GetString()
            : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double Number(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.TryGetDouble(out double number)
            ? number
            : fallback;
}

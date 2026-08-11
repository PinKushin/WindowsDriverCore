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

                    PointerRefusal? moved = Move(kind, x, y, toX, toY, down);
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
                return PointerRefusal.Reason("The system refused a contact update");
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

        PointerRefusal? moved = Move(SyntheticPointerKind.Touch, fromX, fromY, toX, toY, down: true);
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

    private PointerRefusal? Move(SyntheticPointerKind kind, int fromX, int fromY, int toX, int toY, bool down)
    {
        if (!down)
        {
            // Not in contact, so there is nothing to report. A hovering finger
            // does not exist; a hovering pen does, but reporting it needs
            // INRANGE without INCONTACT and no suite test asks for it yet.
            return null;
        }

        // Interpolated, because a drag is the PATH and not the endpoints. An
        // application measuring velocity or distinguishing a flick from a press
        // sees nothing in a single jump.
        for (int frame = 1; frame <= FramesPerMove; frame++)
        {
            int stepX = fromX + (((toX - fromX) * frame) / FramesPerMove);
            int stepY = fromY + (((toY - fromY) * frame) / FramesPerMove);

            if (!_synthetic.Inject(
                [new SyntheticContact(kind, stepX, stepY, SyntheticContactPhase.Update)]))
            {
                return PointerRefusal.Reason("The system refused a contact update");
            }
        }

        return null;
    }

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
        string? elementId = ElementId(origin);
        if (elementId is null)
        {
            return (0, 0, PointerRefusal.Reason(
                "The origin names an element this session does not know"));
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

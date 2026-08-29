using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>The outcome of one vendor command.</summary>
/// <param name="Value">What the command produced, or null for a void one.</param>
/// <param name="Refusal">Why it could not run, or null on success.</param>
/// <remarks>
/// Two fields rather than an exception, for the same reason the rest of this
/// layer avoids them: a refusal is an ordinary answer here and has to reach the
/// client as a message it can act on.
/// </remarks>
public sealed record VendorOutcome(object? Value, string? Refusal)
{
    /// <summary>A command that did what was asked and has nothing to report.</summary>
    public static VendorOutcome Done { get; } = new(null, null);

    /// <summary>A command that produced a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The outcome.</returns>
    public static VendorOutcome Produced(object? value) => new(value, null);

    /// <summary>A command that could not run, and why.</summary>
    /// <param name="why">The message the client sees.</param>
    /// <returns>The outcome.</returns>
    public static VendorOutcome Refused(string why) => new(null, why);
}

/// <summary>
/// The <c>windows:</c> vendor commands served over <c>POST /execute</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This goes BEYOND WinAppDriver rather than matching it, and the measurement
/// matters because this repository previously claimed the opposite.</b> Probed
/// 2026-08-29 against WinAppDriver 1.2.2009 in the guest: <c>POST /execute</c>
/// answers <b>501</b> for every script including <c>windows: click</c> and
/// <c>windows: keys</c>, while an invented route answers 404. So the reference
/// ROUTES the command and implements nothing — it has no vendor vocabulary at
/// all.
/// </para>
/// <para>
/// The <c>windows:</c> names come from <b>appium-windows-driver</b>, the Node
/// driver that wraps WinAppDriver, and that is the client population this serves.
/// Matching its spelling rather than inventing one is the whole point: an
/// existing Appium suite should point here unchanged.
/// </para>
/// <para>
/// <b>A 501 is a limitation, not a contract.</b> The project rule is to match the
/// API and fix the behaviour, which is why serving these is in scope while
/// reproducing the 501 would not be.
/// </para>
/// <para>
/// <b>What is deliberately NOT here: arbitrary code execution.</b>
/// appium-windows-driver has <c>windows: execPowerShell</c>, which runs a shell
/// command on the machine hosting the driver. That is remote code execution by
/// design, reachable by anything that can open a socket to this process — and
/// this driver binds a TCP port. It is not served, and adding it would be an
/// explicit decision with an explicit opt-in switch, not a completeness tick.
/// See <c>docs/DECISIONS.md</c>.
/// </para>
/// </remarks>
public sealed class VendorCommandRunner
{
    /// <summary>The prefix every vendor command carries.</summary>
    private const string Prefix = "windows: ";

    private readonly IPointerInput? _pointer;
    private readonly IKeyboardInput? _keyboard;
    private readonly IWindowLocator _windows;
    private readonly IElementInspector _inspector;
    private readonly IClipboard? _clipboard;

    /// <summary>Creates a runner.</summary>
    /// <param name="windows">Window geometry and ownership.</param>
    /// <param name="inspector">Reads element positions for element-relative commands.</param>
    /// <param name="mouse">The mouse, or null when none is registered.</param>
    /// <param name="clipboard">The clipboard, or null when none is registered.</param>
    /// <param name="keyboard">The keyboard, or null when none is registered.</param>
    public VendorCommandRunner(
        IWindowLocator windows,
        IElementInspector inspector,
        IPointerInput? mouse = null,
        IKeyboardInput? keyboard = null,
        IClipboard? clipboard = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(inspector);

        _windows = windows;
        _inspector = inspector;
        _pointer = mouse;
        _keyboard = keyboard;
        _clipboard = clipboard;
    }

    /// <summary>The commands this driver serves, for the refusal message.</summary>
    /// <remarks>
    /// Named in the refusal rather than kept private, because "unknown command"
    /// without a list is the least actionable error a client can get — and the
    /// vocabulary is not guessable.
    /// </remarks>
    public static IReadOnlyList<string> Supported { get; } =
    [
        "windows: click",
        "windows: doubleClick",
        "windows: hover",
        "windows: scroll",
        "windows: clickAndDrag",
        "windows: keys",
        "windows: getClipboard",
        "windows: setClipboard",
    ];

    /// <summary>Runs one script.</summary>
    /// <param name="script">The <c>script</c> field of the request.</param>
    /// <param name="argument">The first element of <c>args</c>, or null.</param>
    /// <param name="session">The session, for its window and held modifiers.</param>
    /// <returns>What happened.</returns>
    public VendorOutcome Run(string? script, JsonElement? argument, DriverSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(script))
        {
            return VendorOutcome.Refused(
                "An empty script is not a command. " + WhatIsSupported);
        }

        if (!script.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // A UIA TREE IS NOT A DOCUMENT AND THERE IS NO SCRIPT ENGINE.
            // Refused with a reason rather than the reference's bare 501, so a
            // client sending browser JavaScript learns why instead of guessing.
            return VendorOutcome.Refused(
                "This driver has no script engine: a UI Automation tree is not a document, " +
                "so there is nothing for JavaScript to run against. " + WhatIsSupported);
        }

        return script[Prefix.Length..] switch
        {
            "click" => Click(argument, session, times: 1),
            "doubleClick" => Click(argument, session, times: 2),
            "hover" => Hover(argument, session),
            "scroll" => Scroll(argument, session),
            "clickAndDrag" => ClickAndDrag(argument, session),
            "keys" => Keys(argument, session),
            "getClipboard" => GetClipboard(),
            "setClipboard" => SetClipboard(argument),
            _ => VendorOutcome.Refused($"Unknown command '{script}'. {WhatIsSupported}"),
        };
    }

    private static string WhatIsSupported =>
        "Supported: " + string.Join(", ", Supported) + ".";

    /// <summary>A click, optionally repeated and under held modifiers.</summary>
    /// <remarks>
    /// <para>
    /// <c>times</c> is honoured rather than mapped onto a double-click API,
    /// because a caller asking for three is asking for three. Two goes through
    /// the double-click path so the system's own double-click TIME applies —
    /// two separate clicks a few milliseconds apart are not the same event.
    /// </para>
    /// </remarks>
    private VendorOutcome Click(JsonElement? argument, DriverSession session, int times)
    {
        if (_pointer is null)
        {
            return VendorOutcome.Refused(NoPointer);
        }

        (int x, int y, string? refusal) = PointFor(argument, session);
        if (refusal is not null)
        {
            return VendorOutcome.Refused(refusal);
        }

        if (!_pointer.MoveTo(x, y))
        {
            return VendorOutcome.Refused("The pointer could not be moved to that point");
        }

        PointerButton button = ButtonFrom(argument);
        int repeats = Whole(argument, "times", times);

        string? modifiers = Text(argument, "modifierKeys");
        HeldModifiers held = new();

        // MODIFIERS ARE HELD ACROSS THE CLICKS AND LIFTED AFTER, which is the
        // whole reason a caller states them here rather than sending /keys
        // around the click. Held with the same toggle the keyboard already uses.
        bool holding = modifiers is { Length: > 0 } && _keyboard is not null;

        if (holding)
        {
            _keyboard!.Type(modifiers!, held);
        }

        try
        {
            for (int index = 0; index < repeats; index++)
            {
                bool sent = repeats == 2 && index == 0
                    ? _pointer.DoubleClick(button)
                    : _pointer.Click(button);

                if (!sent)
                {
                    return VendorOutcome.Refused("The click could not be dispatched");
                }

                // A double-click is ONE call, so the loop is done after it.
                if (repeats == 2 && index == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            // Lifted even on a refusal. A modifier left down by a failed command
            // applies to everything the session does afterwards, and to the
            // desktop after the session ends.
            //
            // GATED ON WHAT WE ASKED FOR, not on what the held set reports. The
            // set is populated by the keyboard, so releasing only when it is
            // non-empty makes the cleanup depend on the collaborator having done
            // its half - and a keyboard that pressed the keys but did not record
            // them would leak a held modifier with nothing able to detect it.
            // Our own intent is the thing we can be sure of.
            if (holding)
            {
                _keyboard!.ReleaseHeld(held);
            }
        }

        return VendorOutcome.Done;
    }

    private VendorOutcome Hover(JsonElement? argument, DriverSession session)
    {
        if (_pointer is null)
        {
            return VendorOutcome.Refused(NoPointer);
        }

        (int x, int y, string? refusal) = PointFor(argument, session);

        if (refusal is not null)
        {
            return VendorOutcome.Refused(refusal);
        }

        return _pointer.MoveTo(x, y)
            ? VendorOutcome.Done
            : VendorOutcome.Refused("The pointer could not be moved to that point");
    }

    /// <summary>A mouse wheel turn, which is not a touch gesture.</summary>
    /// <remarks>
    /// <c>deltaX</c> and <c>deltaY</c> are stated the way the WHEEL turns —
    /// positive <c>deltaY</c> rotates away from the user and scrolls content up.
    /// That is Win32's convention and appium-windows-driver's, and it is the
    /// OPPOSITE of W3C's <c>wheel</c> action, where positive scrolls down. The
    /// two callers translate separately rather than sharing a guess.
    /// </remarks>
    private VendorOutcome Scroll(JsonElement? argument, DriverSession session)
    {
        if (_pointer is null)
        {
            return VendorOutcome.Refused(NoPointer);
        }

        (int x, int y, string? refusal) = PointFor(argument, session);
        if (refusal is not null)
        {
            return VendorOutcome.Refused(refusal);
        }

        // THE WHEEL TURNS WHERE THE POINTER IS, so the move is part of the
        // command rather than something the caller has to remember. A wheel event
        // is delivered to the window under the cursor, not to the focused one.
        if (!_pointer.MoveTo(x, y))
        {
            return VendorOutcome.Refused("The pointer could not be moved to that point");
        }

        int deltaX = Whole(argument, "deltaX", 0);
        int deltaY = Whole(argument, "deltaY", 0);

        if (deltaX == 0 && deltaY == 0)
        {
            return VendorOutcome.Refused(
                "\"deltaX\" or \"deltaY\" must be a non-zero whole number of wheel notches");
        }

        return _pointer.Scroll(deltaX, deltaY)
            ? VendorOutcome.Done
            : VendorOutcome.Refused("The wheel input could not be dispatched");
    }

    /// <summary>Press at one point, move, release at another.</summary>
    private VendorOutcome ClickAndDrag(JsonElement? argument, DriverSession session)
    {
        if (_pointer is null)
        {
            return VendorOutcome.Refused(NoPointer);
        }

        (int fromX, int fromY, string? start) = PointFor(argument, session, "start");
        if (start is not null)
        {
            return VendorOutcome.Refused(start);
        }

        (int toX, int toY, string? end) = PointFor(argument, session, "end");
        if (end is not null)
        {
            return VendorOutcome.Refused(end);
        }

        if (!_pointer.MoveTo(fromX, fromY) || !_pointer.Press(PointerButton.Left))
        {
            return VendorOutcome.Refused("The drag could not be started");
        }

        // MoveTo walks a path while the button is down - a single jump is not a
        // drag any window manager can follow, which is measured and is why the
        // mouse path interpolates.
        bool moved = _pointer.MoveTo(toX, toY);
        bool released = _pointer.Release(PointerButton.Left);

        // RELEASED EVEN IF THE MOVE FAILED, and the move's result is still
        // reported. Returning early on a failed move would leave the left button
        // physically down on the desktop.
        return moved && released
            ? VendorOutcome.Done
            : VendorOutcome.Refused("The drag could not be completed");
    }

    /// <summary>Key input, stated as appium-windows-driver states it.</summary>
    /// <remarks>
    /// <para>
    /// The argument is <c>{"actions": [...]}</c> where each action carries either
    /// <c>text</c> (typed as-is) or a <c>virtualKeyCode</c> with optional
    /// <c>down</c>, or <c>pause</c> with a duration.
    /// </para>
    /// <para>
    /// <b>A pause is refused rather than slept through.</b> This driver has no
    /// sleep in an input path by rule — waits synchronise on a condition — and a
    /// pause between keystrokes is a request for a clock. Saying so is better
    /// than silently ignoring it, which would report a timed sequence was
    /// delivered untimed.
    /// </para>
    /// </remarks>
    private VendorOutcome Keys(JsonElement? argument, DriverSession session)
    {
        if (_keyboard is null)
        {
            return VendorOutcome.Refused("No keyboard is registered on this server");
        }

        if (argument is not { } arguments ||
            !arguments.TryGetProperty("actions", out JsonElement actions) ||
            actions.ValueKind != JsonValueKind.Array)
        {
            return VendorOutcome.Refused(
                "\"actions\" must be an array of key actions");
        }

        StringBuilder typed = new();

        foreach (JsonElement action in actions.EnumerateArray())
        {
            if (action.TryGetProperty("pause", out _))
            {
                return VendorOutcome.Refused(
                    "\"pause\" is not served: this driver has no timed waits in an input path, " +
                    "and delivering the keys untimed would report something that did not happen");
            }

            if (action.TryGetProperty("text", out JsonElement text) &&
                text.ValueKind == JsonValueKind.String)
            {
                typed.Append(text.GetString());
                continue;
            }

            if (!action.TryGetProperty("virtualKeyCode", out JsonElement code) ||
                !code.TryGetInt32(out int virtualKey))
            {
                return VendorOutcome.Refused(
                    "each action needs \"text\" or an integer \"virtualKeyCode\"");
            }

            string? character = KeyboardKeys.ForVirtualKey(virtualKey);

            if (character is null)
            {
                return VendorOutcome.Refused(
                    $"virtualKeyCode {virtualKey} has no WebDriver spelling; " +
                    "send it as \"text\" instead");
            }

            typed.Append(character);
        }

        if (typed.Length == 0)
        {
            return VendorOutcome.Done;
        }

        return _keyboard.Type(typed.ToString(), session.Modifiers)
            ? VendorOutcome.Done
            : VendorOutcome.Refused("The keystrokes could not be dispatched");
    }

    private VendorOutcome GetClipboard()
    {
        if (_clipboard is null)
        {
            return VendorOutcome.Refused(NoClipboard);
        }

        // A FAILED READ IS NOT AN EMPTY CLIPBOARD. The clipboard holding an
        // image, or being held open by another process, both answer false - and
        // returning "" for either would tell the caller it was empty.
        return _clipboard.TryRead(out string? content)
            ? VendorOutcome.Produced(content)
            : VendorOutcome.Refused("The clipboard holds no text, or could not be read");
    }

    private VendorOutcome SetClipboard(JsonElement? argument)
    {
        string? content = Text(argument, "b64Content") is { } encoded
            ? Decode(encoded)
            : Text(argument, "content");

        if (content is null)
        {
            return VendorOutcome.Refused(
                "\"content\", or \"b64Content\" for base64, must be a string");
        }

        if (_clipboard is null)
        {
            return VendorOutcome.Refused(NoClipboard);
        }

        return _clipboard.TryWrite(content)
            ? VendorOutcome.Done
            : VendorOutcome.Refused("The clipboard could not be written");
    }

    /// <summary>Base64 in, text out, or null when it is not base64.</summary>
    /// <remarks>
    /// appium-windows-driver takes clipboard content base64-encoded, which is how
    /// a client sends bytes that would not survive JSON. Malformed input is
    /// refused rather than treated as literal text, because silently pasting
    /// "SGVsbG8=" is worse than saying it did not decode.
    /// </remarks>
    private static string? Decode(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private const string NoPointer = "No pointer injector is registered on this server";

    private const string NoClipboard = "No clipboard is registered on this server";

    /// <summary>Where a command acts, from an element id or a coordinate pair.</summary>
    /// <remarks>
    /// <para>
    /// Three forms, in the order appium-windows-driver resolves them: an
    /// <c>elementId</c>, whose centre is used; an <c>x</c>/<c>y</c> pair, which is
    /// relative to the SESSION WINDOW rather than the screen; or neither, which
    /// means wherever the pointer already is.
    /// </para>
    /// <para>
    /// <b>Window-relative, and this is the one that has bitten before.</b> A
    /// viewport origin is measured from the window, so treating x and y as screen
    /// coordinates sends real input into whatever application happens to be
    /// there. The pointer path had no such guard once and clicked into another
    /// program.
    /// </para>
    /// </remarks>
    private (int X, int Y, string? Refusal) PointFor(
        JsonElement? argument, DriverSession session, string prefix = "")
    {
        string elementKey = prefix.Length == 0 ? "elementId" : prefix + "ElementId";
        string xKey = prefix.Length == 0 ? "x" : prefix + "X";
        string yKey = prefix.Length == 0 ? "y" : prefix + "Y";

        if (Text(argument, elementKey) is { Length: > 0 } elementId)
        {
            // SCREEN bounds, because the pointer is driven in screen pixels.
            // The window-relative pair exists for /element/{id}/location, which
            // answers a different question.
            ElementRead<ElementBounds> bounds =
                _inspector.ScreenBounds(session.WindowHandle, elementId);

            if (bounds.Outcome != ElementReadOutcome.Read)
            {
                return (0, 0, $"Element '{elementId}' could not be located");
            }

            // The CENTRE, which is where a click on an element means. A corner is
            // inside the rectangle and outside most controls' hit area.
            ElementBounds box = bounds.Value;
            return (box.X + (box.Width / 2), box.Y + (box.Height / 2), null);
        }

        if (argument is { } arguments &&
            arguments.TryGetProperty(xKey, out JsonElement _) &&
            arguments.TryGetProperty(yKey, out JsonElement _))
        {
            WindowBounds? window = _windows.GetBounds(session.WindowHandle);

            if (window is null)
            {
                return (0, 0, "The session window could not be placed");
            }

            int x = window.X + Whole(argument, xKey, 0);
            int y = window.Y + Whole(argument, yKey, 0);

            // THE SAME GUARD THE POINTER PATH LEARNED THE HARD WAY. A coordinate
            // outside the session's own window is not this session's to click.
            return _windows.OwnsThePointAt(x, y, session.WindowHandle)
                ? (x, y, null)
                : (0, 0,
                    $"({x}, {y}) is outside the session window, so it is not this session's to act on");
        }

        // Wherever the pointer already is. Legal, and how a caller chains a hover
        // and then a click without restating the point.
        return _pointer is not null && _pointer.TryGetPosition(out int currentX, out int currentY)
            ? (currentX, currentY, null)
            : (0, 0, "No point was given and the pointer's position could not be read");
    }

    private static PointerButton ButtonFrom(JsonElement? argument) =>
        Text(argument, "button") switch
        {
            "right" => PointerButton.Right,
            "middle" => PointerButton.Middle,
            _ => PointerButton.Left,
        };

    private static string? Text(JsonElement? argument, string name) =>
        argument is { } value &&
        value.TryGetProperty(name, out JsonElement found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static int Whole(JsonElement? argument, string name, int fallback) =>
        argument is { } value &&
        value.TryGetProperty(name, out JsonElement found) &&
        found.ValueKind == JsonValueKind.Number &&
        found.TryGetInt32(out int number)
            ? number
            : fallback;
}

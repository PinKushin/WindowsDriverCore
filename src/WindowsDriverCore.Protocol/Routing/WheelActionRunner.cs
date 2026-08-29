using System.Text.Json;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Performs the <c>wheel</c> input sources of a W3C action sequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third implementation behind one route, and the second one that was
/// missing.</b> <c>/actions</c> takes three source types — <c>pointer</c>,
/// <c>key</c> and <c>wheel</c> — and this driver performed only the first while
/// answering 200 to all three. Key sources were found by the audit's
/// by-parameter lens; this one turned up while adding a mouse wheel for
/// <c>windows: scroll</c>, which is to say it was found by accident.
/// </para>
/// <para>
/// A peer of <see cref="PointerActionRunner"/> and <see cref="KeyActionRunner"/>
/// rather than part of either: they share a payload and nothing else.
/// </para>
/// </remarks>
public sealed class WheelActionRunner
{
    private readonly IPointerInput _mouse;
    private readonly IWindowLocator _windows;

    /// <summary>Creates a runner.</summary>
    /// <param name="mouse">Where the wheel input goes.</param>
    /// <param name="windows">Window geometry, for the viewport origin.</param>
    public WheelActionRunner(IPointerInput mouse, IWindowLocator windows)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        ArgumentNullException.ThrowIfNull(windows);

        _mouse = mouse;
        _windows = windows;
    }

    /// <summary>True if the payload has a source this runner owns.</summary>
    /// <param name="payload">The action sequence.</param>
    public static bool HasWheelSource(JsonElement payload) =>
        KeyActionRunner.HasSourceOfType(payload, "wheel");

    /// <summary>Performs every wheel source in the payload.</summary>
    /// <param name="payload">The action sequence.</param>
    /// <param name="window">The session's window, which the origin is measured from.</param>
    /// <returns>A refusal, or null when every scroll was dispatched.</returns>
    public PointerRefusal? Perform(JsonElement payload, nint window)
    {
        if (!payload.TryGetProperty("actions", out JsonElement sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (Text(source, "type") != "wheel" ||
                !source.TryGetProperty("actions", out JsonElement steps) ||
                steps.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement step in steps.EnumerateArray())
            {
                PointerRefusal? refusal = Scroll(step, window);
                if (refusal is not null)
                {
                    return refusal;
                }
            }
        }

        return null;
    }

    private PointerRefusal? Scroll(JsonElement step, nint window)
    {
        // A wheel source's only real action is `scroll`; `pause` is legal and
        // carries no movement. Anything else is skipped rather than refused,
        // because a source may legitimately carry a pause to align its ticks
        // with another device's.
        if (Text(step, "type") != "scroll")
        {
            return null;
        }

        WindowBounds? bounds = _windows.GetBounds(window);

        if (bounds is null)
        {
            return PointerRefusal.Reason(
                "The session window could not be placed, so a viewport coordinate has no meaning");
        }

        // W3C's x and y are VIEWPORT coordinates - measured from the window, not
        // the screen. Treating them as screen coordinates puts real wheel input
        // into whatever application happens to be there.
        int x = bounds.X + Whole(step, "x", 0);
        int y = bounds.Y + Whole(step, "y", 0);

        if (!_windows.OwnsThePointAt(x, y, window))
        {
            return PointerRefusal.Reason(
                $"({x}, {y}) is outside the session window, so it is not this session's to scroll");
        }

        // A wheel event goes to the window UNDER THE CURSOR rather than the
        // focused one, so the move is part of performing the scroll.
        if (!_mouse.MoveTo(x, y))
        {
            return PointerRefusal.Reason("The pointer could not be moved to that point");
        }

        // THE SIGN IS INVERTED, and this is the whole subtlety of the source.
        //
        // W3C states deltaY like a scrollbar position: POSITIVE scrolls content
        // DOWN. Windows states the wheel like a physical wheel: POSITIVE is
        // rotated away from the user, which scrolls content UP. Passing the
        // client's number through unchanged scrolls the wrong way, and a scroll
        // that goes the wrong way still LOOKS like a working scroll.
        //
        // Delta is stated in CSS pixels by W3C and in notches by Win32. One
        // notch is three lines, and browsers report ~100px for it - so the
        // division is the conventional mapping rather than an exact one, and a
        // sub-notch request is rounded up to one notch rather than dropped.
        int deltaX = Notches(Whole(step, "deltaX", 0));
        int deltaY = Notches(Whole(step, "deltaY", 0));

        return _mouse.Scroll(deltaX, -deltaY)
            ? null
            : PointerRefusal.Reason("The wheel input could not be dispatched");
    }

    /// <summary>CSS pixels as wheel notches, never rounding a request to nothing.</summary>
    /// <remarks>
    /// A caller asking for 30 px means to scroll; answering with zero notches
    /// would dispatch a wheel event that turned nothing and report success. So
    /// any non-zero request becomes at least one notch, in the direction asked.
    /// </remarks>
    private static int Notches(int pixels)
    {
        if (pixels == 0)
        {
            return 0;
        }

        int whole = pixels / PixelsPerNotch;

        return whole != 0 ? whole : Math.Sign(pixels);
    }

    /// <summary>CSS pixels in one wheel notch, as browsers report it.</summary>
    private const int PixelsPerNotch = 100;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Whole(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : fallback;
}

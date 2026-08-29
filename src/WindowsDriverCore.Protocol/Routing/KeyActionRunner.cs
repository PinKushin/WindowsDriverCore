using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Performs the <c>key</c> input sources of a W3C action sequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>A peer of <see cref="PointerActionRunner"/>, not a part of it.</b> They
/// share a payload and nothing else: one talks to
/// <see cref="IKeyboardInput"/> and the other to
/// <see cref="IPointerInput"/>, and a mixed sequence is two independent halves.
/// </para>
/// <para>
/// <b>Why this class exists at all.</b> Key sources were skipped, with a comment
/// saying they were someone else's job — and there was no someone else. So a
/// Selenium 4 <c>ActionChains</c> keyboard sequence, which is how that client
/// sends every keystroke, was validated, skipped, and answered 200 having typed
/// nothing. The compatibility suite is Selenium 3.8 and types through
/// <c>/keys</c>, so no score could ever have shown it.
/// </para>
/// </remarks>
public sealed class KeyActionRunner
{
    /// <summary>The modifier characters, which toggle rather than press.</summary>
    /// <remarks>
    /// Both dialects use the same Unicode private-use block for special keys, so
    /// a W3C <c>value</c> of U+E009 and a JSON Wire <c>/keys</c> character of
    /// U+E009 are the same control. That is what makes the translation below a
    /// conversion of SEMANTICS rather than of encoding.
    /// </remarks>
    private static readonly HashSet<char> ModifierKeys =
    [
        '', // shift
        '', // control
        '', // alt
        '', // meta / windows
    ];

    private readonly IKeyboardInput _keyboard;

    /// <summary>Creates a runner over a keyboard.</summary>
    /// <param name="keyboard">Where the keystrokes go.</param>
    public KeyActionRunner(IKeyboardInput keyboard)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        _keyboard = keyboard;
    }

    /// <summary>True if the payload has a source this runner owns.</summary>
    /// <param name="payload">The action sequence.</param>
    /// <remarks>
    /// Asked before performing, so a payload with no keyboard half does not
    /// report a keyboard failure it never attempted.
    /// </remarks>
    public static bool HasKeySource(JsonElement payload) => HasSourceOfType(payload, "key");

    /// <summary>True if the payload declares an input source of a given type.</summary>
    /// <param name="payload">The action sequence.</param>
    /// <param name="type">The source type, <c>key</c> or <c>pointer</c>.</param>
    /// <remarks>
    /// Shared by both runners rather than written twice.
    /// </remarks>
    internal static bool HasSourceOfType(JsonElement payload, string type) =>
        payload.TryGetProperty("actions", out JsonElement sources) &&
        sources.ValueKind == JsonValueKind.Array &&
        sources.EnumerateArray().Any(source => Text(source, "type") == type);

    /// <summary>Performs every key source in the payload.</summary>
    /// <param name="payload">The action sequence.</param>
    /// <param name="held">The modifiers the session is already holding.</param>
    /// <returns>False if the keyboard refused the keystrokes.</returns>
    /// <remarks>
    /// <paramref name="held"/> is read AND written: it seeds the translation, so
    /// a <c>keyDown</c> of a modifier already down emits nothing, and it is
    /// updated by the keyboard with whatever the sequence left held.
    /// </remarks>
    public bool Perform(JsonElement payload, HeldModifiers held)
    {
        ArgumentNullException.ThrowIfNull(held);

        if (!payload.TryGetProperty("actions", out JsonElement sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (Text(source, "type") != "key" ||
                !source.TryGetProperty("actions", out JsonElement steps) ||
                steps.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            string keys = Translate(steps, held);

            // A source of nothing but pauses types nothing, and asking the
            // keyboard to send an empty batch would report a refusal for a
            // sequence that had nothing to send.
            if (keys.Length > 0 && !_keyboard.Type(keys, held))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>W3C's explicit events as the toggling string the keyboard takes.</summary>
    /// <remarks>
    /// <para>
    /// <b>The two models disagree and cannot be concatenated.</b> W3C says
    /// <c>keyDown</c> and <c>keyUp</c> outright; the JSON Wire string this
    /// driver already types treats every occurrence of a modifier as a FLIP of
    /// its held state. Appending each <c>value</c> in order would turn
    /// down-Ctrl, down-a, up-a, up-Ctrl into <c>Ctrl a a Ctrl</c> — two
    /// keystrokes where the client asked for one.
    /// </para>
    /// <para>
    /// So each event is converted by what it CHANGES:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A modifier emits its character only when the held state actually moves.
    /// Pressing one already down emits nothing, which is what stops a repeat
    /// from silently releasing it.
    /// </description></item>
    /// <item><description>
    /// An ordinary key emits on <c>keyDown</c> and nothing on <c>keyUp</c> — the
    /// keyboard sends a down and an up for each character, so the pair is
    /// already one keystroke.
    /// </description></item>
    /// <item><description>
    /// <c>pause</c> emits nothing. It carries no <c>value</c>, and reading one
    /// anyway would type a stray character.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static string Translate(JsonElement steps, HeldModifiers held)
    {
        StringBuilder keys = new();

        // A COPY, deliberately. The keyboard does the real toggling against
        // `held` when the batch is sent; this set only decides what to emit, and
        // mutating the session's own state here would double-count every flip.
        HashSet<char> down = [.. held.All];

        foreach (JsonElement step in steps.EnumerateArray())
        {
            string? action = Text(step, "type");

            if (action is not ("keyDown" or "keyUp") ||
                Text(step, "value") is not { Length: > 0 } value)
            {
                continue;
            }

            char key = value[0];

            if (!ModifierKeys.Contains(key))
            {
                if (action == "keyDown")
                {
                    keys.Append(key);
                }

                continue;
            }

            bool moved = action == "keyDown" ? down.Add(key) : down.Remove(key);

            if (moved)
            {
                keys.Append(key);
            }
        }

        return keys.ToString();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

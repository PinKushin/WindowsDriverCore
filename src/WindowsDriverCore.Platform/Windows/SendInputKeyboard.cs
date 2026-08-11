using System.Collections.Generic;

namespace WindowsDriverCore.Platform.Windows;

/// <inheritdoc cref="IKeyboardInput" />
/// <remarks>
/// <para>
/// <b>Printable characters go through as Unicode, not as virtual keys.</b>
/// <c>KEYEVENTF_UNICODE</c> delivers the character itself, so "A" arrives as "A"
/// on a Dvorak or AZERTY layout too. Mapping characters to virtual-key codes is
/// the version that works on the developer's keyboard and silently types
/// something else on somebody else's.
/// </para>
/// <para>
/// <b>Modifiers toggle rather than press.</b> WebDriver sends a modifier as a
/// character in the stream and each occurrence flips its held state, so
/// <c>Control a Control</c> is hold, press, release. Anything still held at the
/// end is released, because leaving control down would poison every later
/// keystroke in the session — and that failure appears in a different test than
/// the one that caused it.
/// </para>
/// </remarks>
public sealed class SendInputKeyboard : IKeyboardInput
{
    private const uint InputKeyboard = 1;
    private const uint ExtendedKey = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;

    /// <summary>
    /// Virtual keys that must carry <c>KEYEVENTF_EXTENDEDKEY</c>.
    /// </summary>
    /// <remarks>
    /// <b>Measured 2026-08-10.</b> Without this flag the navigation and editing
    /// keys are delivered as their non-extended twins — which share virtual-key
    /// codes with the numeric keypad — and applications mostly ignore them. The
    /// symptom was a compatibility test that typed its text successfully and then
    /// had every Delete, Home and arrow key do nothing:
    /// <c>SendKeys_NonPrintableKeys</c> expected an edited string and observed
    /// "aaaaaaaa", the raw text with none of the edits applied.
    /// </remarks>
    private static readonly HashSet<ushort> ExtendedKeys =
    [
        0x21, // page up
        0x22, // page down
        0x23, // end
        0x24, // home
        0x25, // left
        0x26, // up
        0x27, // right
        0x28, // down
        0x2D, // insert
        0x2E, // delete
    ];

    /// <summary>WebDriver's private-use key codes, as virtual-key codes.</summary>
    private static readonly Dictionary<char, ushort> SpecialKeys = new()
    {
        [''] = 0x08,  // backspace
        [''] = 0x09,  // tab
        [''] = 0x0D,  // return
        [''] = 0x0D,  // enter
        [''] = 0x1B,  // escape
        [''] = 0x20,  // space
        [''] = 0x21,  // page up
        [''] = 0x22,  // page down
        [''] = 0x23,  // end
        [''] = 0x24,  // home
        [''] = 0x25,  // left
        [''] = 0x26,  // up
        [''] = 0x27,  // right
        [''] = 0x28,  // down
        [''] = 0x2D,  // insert
        [''] = 0x2E,  // delete
    };

    /// <summary>Modifiers, which toggle rather than press.</summary>
    private static readonly Dictionary<char, ushort> Modifiers = new()
    {
        [''] = 0x10,  // shift
        [''] = 0x11,  // control
        [''] = 0x12,  // alt
        [''] = 0x5B,  // meta / windows
    };

    /// <inheritdoc />
    public bool Type(string keys) => Send(BuildBatch(keys, null));

    /// <inheritdoc />
    public bool Type(string keys, HeldModifiers held)
    {
        ArgumentNullException.ThrowIfNull(held);

        return Send(BuildBatch(keys, held));
    }

    /// <inheritdoc />
    public bool ReleaseHeld(HeldModifiers held)
    {
        ArgumentNullException.ThrowIfNull(held);

        List<Win32.Input> batch = [];

        foreach (char modifier in held.All)
        {
            if (Modifiers.TryGetValue(modifier, out ushort code))
            {
                batch.Add(VirtualKey(code, down: false));
            }
        }

        held.Clear();
        return Send([.. batch]);
    }

    private static bool Send(Win32.Input[] inputs)
    {

        if (inputs.Length == 0)
        {
            // An empty sequence is a valid request that types nothing. The suite
            // sends one deliberately.
            return true;
        }

        uint sent = Win32.SendInput(
            (uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Win32.Input>());

        return sent == inputs.Length;
    }

    /// <summary>Turns a key sequence into the input batch that expresses it.</summary>
    /// <remarks>
    /// Separate from sending so it can be TESTED. Verifying this by actually
    /// typing would deliver keystrokes to whatever window has focus on the
    /// machine running the tests, including the developer's editor — so the
    /// decisions live here and the dispatch stays trivial.
    /// </remarks>
    private static Win32.Input[] BuildBatch(string keys, HeldModifiers? carried)
    {
        ArgumentNullException.ThrowIfNull(keys);

        List<Win32.Input> batch = [];

        // SEEDED FROM THE SESSION, and not pressed again. A modifier carried in
        // is already physically down because the previous call deliberately did
        // not lift it; re-pressing it would send a second key-down that the
        // application sees as a repeat.
        HashSet<char> held = carried is null ? [] : [.. carried.All];

        foreach (char key in keys)
        {
            if (Modifiers.TryGetValue(key, out ushort modifier))
            {
                if (held.Add(key))
                {
                    batch.Add(VirtualKey(modifier, down: true));
                }
                else
                {
                    held.Remove(key);
                    batch.Add(VirtualKey(modifier, down: false));
                }

                continue;
            }

            if (SpecialKeys.TryGetValue(key, out ushort special))
            {
                batch.Add(VirtualKey(special, down: true));
                batch.Add(VirtualKey(special, down: false));
                continue;
            }

            // UNDER A HELD MODIFIER, A CHARACTER MUST BE A VIRTUAL KEY.
            //
            // KEYEVENTF_UNICODE injects the character directly and bypasses the
            // keyboard layout, so modifier state does NOT combine with it: with
            // control held, an injected "a" arrives as the letter rather than as
            // Ctrl+A. The compatibility suite clears its edit box with Ctrl+A then
            // Delete, so every clear appended one more "a" instead of selecting
            // anything - measured, the residue grew by exactly one per test, <a>
            // in the first through <aaaaaaaaaaaa> in the twelfth, and took 24
            // tests with it.
            //
            // The shift bit VkKeyScan reports is deliberately ignored. It matters
            // for producing the right CHARACTER, and this path exists for
            // shortcuts, where Ctrl+A and Ctrl+Shift+A are different commands and
            // the caller asked for the former by writing "a".
            if (held.Count > 0)
            {
                short mapped = Win32.VkKeyScan((ushort)key);
                if (mapped != -1)
                {
                    ushort virtualKey = (ushort)(mapped & 0xFF);
                    batch.Add(VirtualKey(virtualKey, down: true));
                    batch.Add(VirtualKey(virtualKey, down: false));
                    continue;
                }

                // No key for it in this layout. Unicode is still the better
                // answer than dropping the keystroke, even though the modifier
                // will not apply.
            }

            batch.Add(UnicodeKey(key, down: true));
            batch.Add(UnicodeKey(key, down: false));
        }

        if (carried is null)
        {
            // THE ELEMENT CONTRACT. Release anything the sequence left held:
            // leaving control down would corrupt every later keystroke, and that
            // shows up in a different test than the one that caused it. The suite
            // states it — "SendKeys implicitly depress all modifier at the end of
            // the sequence (every API call)".
            foreach (char modifier in held)
            {
                batch.Add(VirtualKey(Modifiers[modifier], down: false));
            }

            return [.. batch];
        }

        // THE SESSION CONTRACT. Nothing is lifted; what is still down is handed
        // back so the next call knows not to press it again. DELETE /session
        // lifts whatever survives, so the desktop never inherits a stuck key.
        carried.Clear();
        foreach (char modifier in held)
        {
            carried.Hold(modifier);
        }

        return [.. batch];
    }

    private static Win32.Input VirtualKey(ushort code, bool down) => new()
    {
        Type = InputKeyboard,
        Union = new Win32.InputUnion
        {
            Keyboard = new Win32.KeyboardInput
            {
                VirtualKey = code,
                ScanCode = 0,
                Flags = (ExtendedKeys.Contains(code) ? ExtendedKey : 0u) | (down ? 0u : KeyUp),
                Time = 0,
                ExtraInfo = 0,
            },
        },
    };

    private static Win32.Input UnicodeKey(char character, bool down) => new()
    {
        Type = InputKeyboard,
        Union = new Win32.InputUnion
        {
            Keyboard = new Win32.KeyboardInput
            {
                VirtualKey = 0,
                ScanCode = character,
                Flags = Unicode | (down ? 0u : KeyUp),
                Time = 0,
                ExtraInfo = 0,
            },
        },
    };
}

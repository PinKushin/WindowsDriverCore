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
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;

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
    public bool Type(string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        Win32.Input[] inputs = BuildBatch(keys);

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
    private static Win32.Input[] BuildBatch(string keys)
    {
        List<Win32.Input> batch = [];
        HashSet<ushort> held = [];

        foreach (char key in keys)
        {
            if (Modifiers.TryGetValue(key, out ushort modifier))
            {
                if (held.Add(modifier))
                {
                    batch.Add(VirtualKey(modifier, down: true));
                }
                else
                {
                    held.Remove(modifier);
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

            batch.Add(UnicodeKey(key, down: true));
            batch.Add(UnicodeKey(key, down: false));
        }

        // Release anything the sequence left held. Leaving control down would
        // corrupt every later keystroke in the session, and that shows up in a
        // different test than the one that caused it.
        foreach (ushort modifier in held)
        {
            batch.Add(VirtualKey(modifier, down: false));
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
                Flags = down ? 0 : KeyUp,
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

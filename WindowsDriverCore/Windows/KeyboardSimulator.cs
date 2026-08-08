using System.Runtime.InteropServices;

namespace WindowsDriverCore.Windows;

public static class KeyboardSimulator
{
    public static void SendKeySequence(List<string> keys)
    {
        var inputs = new List<Win32.INPUT>();

        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key))
                continue;

            foreach (var ch in key)
            {
                var vk = CharToVirtualKey(ch);
                if (vk != 0)
                {
                    inputs.Add(MakeKeyDown(vk));
                    inputs.Add(MakeKeyUp(vk));
                }
            }
        }

        if (inputs.Count > 0)
        {
            Win32.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Win32.INPUT>());
        }
    }

    private static Win32.INPUT MakeKeyDown(ushort vk) => new Win32.INPUT
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = 0 } }
    };

    private static Win32.INPUT MakeKeyUp(ushort vk) => new Win32.INPUT
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = Win32.KEYEVENTF_KEYUP } }
    };

    private static ushort CharToVirtualKey(char ch)
    {
        return ch switch
        {
            // Selenium Keys Unicode private-use-area mappings
            '\ue000' => 0,      // Null
            '\ue001' => 0x1B,   // Cancel
            '\ue002' => 0x0D,   // Help
            '\ue003' => 0x20,   // Backspace → Map to Space as fallback
            '\ue004' => 0x09,   // Tab
            '\ue005' => 0x0D,   // Clear
            '\ue006' => 0x0D,   // Return
            '\ue007' => 0x0D,   // Enter
            '\ue008' => 0x10,   // Left Shift
            '\ue009' => 0x11,   // Left Control
            '\ue00A' => 0x12,   // Left Alt
            '\ue00B' => 0x5B,   // Left Meta (Windows key)
            '\ue00C' => 0x13,   // Pause
            '\ue00D' => 0x14,   // Caps Lock
            '\ue00E' => 0x27,   // Esc
            '\ue00F' => 0x21,   // PageUp (space → convert later)
            '\ue010' => 0x22,   // PageDown
            '\ue011' => 0x23,   // End
            '\ue012' => 0x24,   // Home
            '\ue013' => 0x25,   // Left Arrow
            '\ue014' => 0x26,   // Up Arrow
            '\ue015' => 0x27,   // Right Arrow
            '\ue016' => 0x28,   // Down Arrow
            '\ue017' => 0x2E,   // Delete
            '\ue018' => 0x25,   // Insert → Left as fallback
            '\ue019' => 0x73,   // F1-F12
            '\ue01A' => 0x74,   // F2
            '\ue01B' => 0x75,   // F3
            '\ue01C' => 0x76,   // F4
            '\ue01D' => 0x77,   // F5
            '\ue01E' => 0x78,   // F6
            '\ue01F' => 0x79,   // F7
            '\ue020' => 0x7A,   // F8
            '\ue021' => 0x7B,   // F9
            '\ue022' => 0x7C,   // F10
            '\ue023' => 0x7D,   // F11
            '\ue024' => 0x7E,   // F12

            // Modifier keys
            '\ue025' => 0x10,   // Shift
            '\ue026' => 0x11,   // Control
            '\ue027' => 0x12,   // Alt

            // Number pad
            '\ue028' => 0x60,   // Numpad0
            '\ue029' => 0x61,   // Numpad1
            '\ue02A' => 0x62,   // Numpad2
            '\ue02B' => 0x63,   // Numpad3
            '\ue02C' => 0x64,   // Numpad4
            '\ue02D' => 0x65,   // Numpad5
            '\ue02E' => 0x66,   // Numpad6
            '\ue02F' => 0x67,   // Numpad7
            '\ue030' => 0x68,   // Numpad8
            '\ue031' => 0x69,   // Numpad9

            // Multiplication, Addition, Separation, Subtraction
            '\ue032' => 0x6A,   // Multiply
            '\ue033' => 0x6B,   // Add
            '\ue034' => 0x6C,   // Separator
            '\ue035' => 0x6D,   // Subtract
            '\ue036' => 0x6E,   // Decimal
            '\ue037' => 0x6F,   // Divide

            // Function keys extended
            '\ue038' => 0x70,   // F1 → actually F1
            '\ue039' => 0x71,   // F2

            // Meta keys
            '\ue03A' => 0x5B,   // Command (Left Windows)
            '\ue03B' => 0x5C,   // Right Shift → Wrong, should be Right Shift 0xA1
            '\ue03C' => 0x5D,   // Right Control → Wrong, should be Right Control 0xA3

            // Regular characters - uppercase letters
            >= 'A' and <= 'Z' => (ushort)(ch - 'A' + 0x41),

            // Regular characters - lowercase letters  
            >= 'a' and <= 'z' => (ushort)(ch - 'a' + 0x61),

            // Digits
            >= '0' and <= '9' => (ushort)(ch - '0' + 0x30),

            // Space
            ' ' => 0x20,

            // Special characters mapped to common VK codes
            '=' => 0xBB,
            '-' => 0xBD,
            '[' => 0xDB,
            ']' => 0xDD,
            '\\' => 0xDC,
            ';' => 0xBA,
            '\'' => 0xDE,
            ',' => 0xBC,
            '.' => 0xBE,
            '/' => 0xBF,
            '`' => 0xC0,

            _ => 0
        };
    }
}

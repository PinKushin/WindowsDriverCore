
namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// The WebDriver key characters and the virtual keys they mean.
/// </summary>
/// <remarks>
/// <para>
/// <b>One table, read in both directions.</b> The keyboard turns a character into
/// a virtual key to inject it; <c>windows: keys</c> turns a virtual key back into
/// a character, because appium-windows-driver states keystrokes as
/// <c>virtualKeyCode</c> and this driver types WebDriver's private-use
/// characters.
/// </para>
/// <para>
/// <b>Two tables would drift, and this project has the example.</b>
/// WinAppDriver's own XPath singular and plural were separate implementations of
/// the same question and diverged into issue #1079. A second copy of this map
/// would be the same mistake with a smaller blast radius: a key added to one
/// direction and not the other produces a keystroke that can be sent and not
/// named, or named and not sent.
/// </para>
/// <para>
/// Both dialects use the same Unicode private-use block, so a character here is
/// the JSON Wire spelling AND the W3C one.
/// </para>
/// </remarks>
public static class KeyboardKeys
{
    /// <summary>Keys that press and release, character to virtual key.</summary>
    /// <remarks>
    /// <c>Return</c> and <c>Enter</c> are separate characters that both mean
    /// <c>VK_RETURN</c>, which is why the reverse lookup cannot simply be this
    /// dictionary inverted — see <see cref="ForVirtualKey"/>.
    /// </remarks>
    public static IReadOnlyDictionary<char, ushort> Special { get; } = new Dictionary<char, ushort>
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

    /// <summary>Modifiers, which TOGGLE rather than press.</summary>
    /// <remarks>
    /// Separate from <see cref="Special"/> because the behaviour differs, not
    /// merely the values: a modifier character flips its held state on each
    /// occurrence, so <c>Control a Control</c> is hold, press, release.
    /// </remarks>
    public static IReadOnlyDictionary<char, ushort> Modifiers { get; } = new Dictionary<char, ushort>
    {
        [''] = 0x10,  // shift
        [''] = 0x11,  // control
        [''] = 0x12,  // alt
        [''] = 0x5B,  // meta / windows
    };

    /// <summary>The character that means a virtual key, or null if none does.</summary>
    /// <param name="virtualKey">A Win32 virtual key code.</param>
    /// <returns>The WebDriver character, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>Null rather than a guess.</b> Most virtual keys have no WebDriver
    /// spelling — the function keys, the numeric keypad, media keys — and
    /// returning something plausible would type the wrong key silently. The
    /// caller refuses and names the code instead, which is a message a client can
    /// act on.
    /// </para>
    /// <para>
    /// <b>Built once and cached, and the FIRST spelling wins where two share a
    /// code.</b> Return and Enter are both <c>VK_RETURN</c>; either injects the
    /// same keystroke, so the choice is arbitrary and only needs to be stable.
    /// </para>
    /// </remarks>
    public static string? ForVirtualKey(int virtualKey) =>
        ByVirtualKey.TryGetValue(virtualKey, out string? character) ? character : null;

    private static readonly Dictionary<int, string> ByVirtualKey = Build();

    private static Dictionary<int, string> Build()
    {
        Dictionary<int, string> map = [];

        foreach (KeyValuePair<char, ushort> entry in Special)
        {
            map.TryAdd(entry.Value, entry.Key.ToString());
        }

        foreach (KeyValuePair<char, ushort> entry in Modifiers)
        {
            map.TryAdd(entry.Value, entry.Key.ToString());
        }

        return map;
    }
}

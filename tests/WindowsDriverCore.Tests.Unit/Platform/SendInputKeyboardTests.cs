using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Unit.Platform;

/// <summary>
/// The key sequence a WebDriver payload turns into.
/// </summary>
/// <remarks>
/// <para>
/// <b>These read the batch rather than sending it.</b> Actually typing would
/// deliver keystrokes to whatever window happens to have focus on the machine
/// running the tests — including the developer's editor. The batch is what
/// carries every decision worth checking, so it is built and inspected instead.
/// </para>
/// <para>
/// The property that matters most is <b>modifiers toggle rather than press</b>.
/// WebDriver sends <c>Control a Control</c> meaning hold, press, release; a
/// driver that treated each occurrence as a discrete press would send two
/// control taps and a bare "a", which types nothing useful and looks almost
/// right in a log.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SendInputKeyboardTests
{
    private const char Control = '';
    private const char Shift = '';
    private const char Delete = '';

    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const uint Extended = 0x0001;

    /// <summary>Builds the batch without sending it.</summary>
    /// <remarks>
    /// Reflection, because the batch is an implementation detail that must not
    /// become public API just to be testable — and the alternative is typing
    /// into the developer's machine during a unit test run.
    /// </remarks>
    private static (ushort VirtualKey, ushort ScanCode, uint Flags)[] Batch(string keys)
    {
        MethodInfo? build = typeof(SendInputKeyboard).GetMethod(
            "BuildBatch", BindingFlags.NonPublic | BindingFlags.Static);

        build.ShouldNotBeNull("the batch builder must be reachable for this to test anything");

        object? result = build.Invoke(null, [keys]);
        Win32Input[] inputs = ((Array)result!).Cast<object>()
            .Select(Convert)
            .ToArray();

        return [.. inputs.Select(i => (i.VirtualKey, i.ScanCode, i.Flags))];
    }

    private static Win32Input Convert(object input)
    {
        Type type = input.GetType();
        object union = type.GetField("Union")!.GetValue(input)!;
        object keyboard = union.GetType().GetField("Keyboard")!.GetValue(union)!;

        return new Win32Input(
            (ushort)keyboard.GetType().GetField("VirtualKey")!.GetValue(keyboard)!,
            (ushort)keyboard.GetType().GetField("ScanCode")!.GetValue(keyboard)!,
            (uint)keyboard.GetType().GetField("Flags")!.GetValue(keyboard)!);
    }

    private readonly record struct Win32Input(ushort VirtualKey, ushort ScanCode, uint Flags);

    [Test]
    public void AModifierAppearingTwice_HoldsThenReleases()
    {
        // Control a Control. The middle keystroke must happen while control is
        // still down, which is the whole meaning of the sequence.
        (ushort VirtualKey, ushort ScanCode, uint Flags)[] batch =
            Batch($"{Control}a{Control}");

        batch.Length.ShouldBe(4, "down, a-down, a-up, up");
        batch[0].ShouldBe(((ushort)0x11, (ushort)0, 0u));
        batch[3].ShouldBe(((ushort)0x11, (ushort)0, KeyUp));
    }

    [Test]
    public void APrintableCharacter_IsSentAsUnicode_NotAsAVirtualKey()
    {
        // Virtual-key mapping is the version that works on the developer's
        // keyboard and types something else on a Dvorak or AZERTY layout.
        (ushort VirtualKey, ushort ScanCode, uint Flags)[] batch = Batch("A");

        batch.Length.ShouldBe(2);
        batch[0].VirtualKey.ShouldBe((ushort)0);
        batch[0].ScanCode.ShouldBe((ushort)'A');
        (batch[0].Flags & Unicode).ShouldBe(Unicode);
    }

    [Test]
    public void ASpecialKey_IsSentAsAVirtualKey_NotAsItsPrivateUseCharacter()
    {
        // U+E017 is WebDriver's code for delete. Typing the character itself
        // would put an unprintable glyph into the application.
        (ushort VirtualKey, ushort ScanCode, uint Flags)[] batch = Batch(Delete.ToString());

        batch.Length.ShouldBe(2);
        batch[0].VirtualKey.ShouldBe((ushort)0x2E);
        (batch[0].Flags & Unicode).ShouldBe(0u);
    }

    [Test]
    public void ANavigationKey_CarriesTheExtendedFlag()
    {
        // Delete, the arrows, Home, End and the page keys share virtual-key
        // codes with the numeric keypad. Without KEYEVENTF_EXTENDEDKEY they
        // arrive as the keypad twin and applications mostly ignore them —
        // measured as a compatibility test that typed its text and then had
        // every edit key do nothing.
        (ushort VirtualKey, ushort ScanCode, uint Flags)[] batch = Batch(Delete.ToString());

        (batch[0].Flags & Extended).ShouldBe(Extended);
        (batch[1].Flags & Extended).ShouldBe(Extended, "the key-up needs it too");
    }

    [Test]
    public void ANonNavigationKey_DoesNotCarryIt()
    {
        // The control. Setting the flag on everything would pass the test above
        // and change what unrelated keys mean.
        (ushort VirtualKey, ushort ScanCode, uint Flags)[] batch = Batch($"{Shift}a");

        (batch[0].Flags & Extended).ShouldBe(0u, "shift is not an extended key");
    }

    [Test]
    public void AModifierLeftHeldAtTheEnd_IsReleased()
    {
        // A sequence that opens a modifier and never closes it would otherwise
        // leave control down for the rest of the session, and that corrupts a
        // LATER test rather than this one — the hardest kind of failure to trace.
        (ushort VirtualKey, ushort ScanCode, uint Flags)[] batch = Batch($"{Shift}a");

        batch[^1].ShouldBe(((ushort)0x10, (ushort)0, KeyUp));
    }

    [Test]
    public void AnEmptySequence_TypesNothing()
    {
        // The suite sends one deliberately. Producing a keystroke here would be
        // inventing input the caller never asked for.
        Batch(string.Empty).ShouldBeEmpty();
    }
}

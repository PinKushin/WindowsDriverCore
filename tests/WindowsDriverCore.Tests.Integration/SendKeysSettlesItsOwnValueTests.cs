using System;
using System.Collections.Generic;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// <c>SendKeys</c> waits for the ELEMENT'S OWN value to settle before
/// returning, rather than leaving that to whichever read happens to run next.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and why it is not the same fixture as
/// <see cref="TheDrainWorksMoreThanOnceTests"/>.</b> That fixture calls
/// <c>WaitForInputProcessed</c> itself, exactly as the protocol layer's
/// <c>DrainTypedInput</c> does — it measures the DEFERRED drain, and the drain
/// won it: mutation-removing that call fails it.
/// </para>
/// <para>
/// This fixture calls NOTHING between <c>SendKeys</c> and the read. If the
/// element's value is not already settled by the time <c>SendKeys</c> returns,
/// this fails — and it is meant to, because that is precisely the situation a
/// guest transcript measured: <c>GET /text</c> landing 0.9 ms after the
/// keystroke that should have changed it, with 80 of 101 drains answering in
/// under a millisecond because <c>WaitForInputIdle</c> samples the process
/// before the injected keys reach the target thread.
/// </para>
/// <para>
/// <b>Moving the wait into the WRITE is the point, not an implementation
/// detail.</b> The old contract was "the command answers success; some LATER
/// read might drain enough to see the result." The new one is "the command does
/// not answer success until the value it wrote is actually there" — which does
/// not depend on whatever route happens to run next remembering to drain, and
/// covers <c>Ctrl+A</c> then <c>Delete</c> exactly as well as literal text,
/// because it watches the element's own reported value rather than a typed
/// string it would have to interpret.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("SynthesisesRealInput")]
[NonParallelizable]
public sealed class SendKeysSettlesItsOwnValueTests
{
    private const string TheText = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int Iterations = 5;

    /// <summary>The WebDriver Private-Use-Area codepoint for Backspace, U+E003.</summary>
    /// <remarks>
    /// <b>Named here rather than typed inline at each call site.</b> The literal
    /// character is visually indistinguishable from an ordinary space in most
    /// editors and terminals, which invites exactly the silent corruption this
    /// repository's own rules warn about for hard-to-see edits. One named
    /// constant means the invisible byte exists in exactly one place, with its
    /// codepoint spelled out where a reader can see it. This is the same
    /// codepoint <c>SendInputKeyboard.SpecialKeys</c> decodes and the same one
    /// Selenium's <c>Keys.Backspace</c> sends.
    /// </remarks>
    private const char Backspace = '';

    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private UiaElementInteractor _interactor = null!;
    private nint _window;

    [OneTimeSetUp]
    public void LaunchTheSubject()
    {
        string? path = Win32TestApp.Path;
        if (path is null)
        {
            Assert.Ignore("The Win32 test subject has not been built.");
        }

        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        WindowLocator windows = new();
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);
        _interactor = new UiaElementInteractor(
            automation, resolver, mouse: null, windows, new SendInputKeyboard());

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), windows)
            .Launch(new ApplicationTarget(path, null, null));

        if (launched.Application is null)
        {
            Assert.Fail($"The test subject would not launch: {launched.FailureMessage}");
            return;
        }

        _window = launched.Application.WindowHandle;
        UiSettle.UntilBoundsAreStable(_inspector, _window, EditBox());
    }

    [OneTimeTearDown]
    public void CloseTheSubject() => AppLifetime.KillAll(Win32TestApp.ProcessName);

    private string EditBox() =>
        UiSettle.UntilSomethingMatches(
            _finder, _window, LocatorKind.ControlType, "Edit")[0];

    [Test]
    public void ReadingImmediatelyAfterSendKeysReturns_SeesTheCompleteText_NoDrainCalled()
    {
        List<string> shortReads = [];

        for (int attempt = 1; attempt <= Iterations; attempt++)
        {
            string box = EditBox();
            _interactor.Clear(_window, box);

            _interactor.SendKeys(_window, box, TheText);

            // NOTHING between the write and the read. No WaitForInputProcessed,
            // no session.InputPending, no route-level drain of any kind.
            string read = _inspector.Text(_window, box).Value ?? string.Empty;

            if (!string.Equals(read, TheText, StringComparison.Ordinal))
            {
                shortReads.Add($"attempt {attempt}: {read.Length}/{TheText.Length} chars");
            }
        }

        shortReads.ShouldBeEmpty(
            $"{shortReads.Count} of {Iterations} reads raced SendKeys with no drain in between " +
            "- SendKeys itself must guarantee its own effect is visible before returning");
    }

    /// <summary>
    /// A shrink to empty, read immediately after, with nothing but real Win32
    /// backspace keystrokes doing the deleting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not Ctrl+A then Delete, deliberately.</b> That is what the failing
    /// suite tests actually send - AlarmClockBase.TestInit clears with
    /// Keys.Control + "a" then Keys.Delete - but a bare Win32 EDIT control
    /// created with no accelerator table does not implement Ctrl+A as select-
    /// all on its own; that binding is normally supplied by a dialog's default
    /// accelerator, which this test subject has none of. Measured: sending it
    /// anyway left the text UNCHANGED and growing every iteration, which is
    /// the subject not implementing the shortcut, not a driver race.
    /// </para>
    /// <para>
    /// Backspace IS built into the standard Win32 EDIT window class itself, so
    /// this exercises the same shape - type text, delete it with the keyboard,
    /// read immediately with no drain in between - on a keystroke this subject
    /// genuinely supports. The Ctrl+A/Delete shape is left to the guest suite,
    /// which is the only environment with a subject that actually implements
    /// it.
    /// </para>
    /// </remarks>
    [Test]
    public void DeletingByBackspace_ReadsAsEmpty_ImmediatelyAfter()
    {
        List<string> notEmpty = [];

        for (int attempt = 1; attempt <= Iterations; attempt++)
        {
            string box = EditBox();
            _interactor.SendKeys(_window, box, TheText);

            // One Backspace per typed character, in a single SendKeys call - the
            // same shape as the compatibility suite's own clear, just with a
            // keystroke this subject actually implements.
            _interactor.SendKeys(_window, box, new string(Backspace, TheText.Length));

            string read = _inspector.Text(_window, box).Value ?? string.Empty;
            if (read.Length != 0)
            {
                notEmpty.Add($"attempt {attempt}: {read.Length} chars left, '{read}'");
            }
        }

        notEmpty.ShouldBeEmpty(
            $"{notEmpty.Count} of {Iterations} reads still had text immediately after Backspace");
    }
}

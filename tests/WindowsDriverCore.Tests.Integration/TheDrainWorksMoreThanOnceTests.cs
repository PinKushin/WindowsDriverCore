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
/// The typed-input drain, called repeatedly against ONE long-lived process.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measurement that chose <c>WaitForInputIdle</c> ran it five times from
/// five fresh starts.</b> It never asked whether the second call against the
/// SAME process still waits — and MSDN says it does not:
/// <i>"WaitForInputIdle waits only once for a process to become idle; subsequent
/// WaitForInputIdle calls return immediately, whether the process is idle or
/// busy."</i>
/// </para>
/// <para>
/// If that holds, the drain protects the FIRST read of a suite run and nothing
/// after it. That is the exact shape of the
/// <c>SendKeysToElement_*</c> family: 8/12, 10/12, 8/12 across three guest runs
/// with a different failing set each time, while the session-level
/// <c>SendKeys_*</c> family — which does not read back through this path — is
/// 12/12 every time.
/// </para>
/// <para>
/// <b>The measured variable is the TEXT READ BACK, not how long the wait took.</b>
/// A duration is a proxy: a wait could return quickly and still be correct if
/// the application happened to be finished. The text either came back whole or
/// it did not, and that is the thing the compatibility suite asserts.
/// </para>
/// <para>
/// <b>Iteration 1 is the control.</b> It reproduces the original five-for-five
/// measurement. If it fails, the subject or the harness is wrong and the later
/// iterations say nothing — so the two are reported separately rather than as
/// one pass rate.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("SynthesisesRealInput")]
[NonParallelizable]
public sealed class TheDrainWorksMoreThanOnceTests
{
    /// <summary>Long enough for the effect to exceed the resolution.</summary>
    /// <remarks>
    /// The original probe read back <b>1 of 52</b> characters with no wait at
    /// all, so 52 is a condition where "waited" and "did not wait" predict
    /// visibly different observations. A three-character string would arrive
    /// intact either way and the experiment would be insensitive to its own
    /// manipulation.
    /// </remarks>
    private const string TheText = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private const int Iterations = 5;

    private UiaElementFinder _finder = null!;
    private UiaElementInspector _inspector = null!;
    private UiaElementInteractor _interactor = null!;
    private WindowLocator _windows = null!;
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
        _windows = new WindowLocator();
        _finder = new UiaElementFinder(automation, resolver);
        _inspector = new UiaElementInspector(automation, resolver);
        _interactor = new UiaElementInteractor(
            automation, resolver, mouse: null, _windows, new SendInputKeyboard());

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), _windows)
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

    /// <summary>Clears, types, drains, and reports what came back.</summary>
    private string TypeThenDrainThenRead()
    {
        string box = EditBox();

        _interactor.Clear(_window, box);
        _interactor.SendKeys(_window, box, TheText);

        // The drain under test, called exactly as the /text route calls it.
        //
        // MUTATION-VERIFIED: removing this line fails BOTH tests here. One
        // attempt read back 54 of 52 characters - more than were typed - so the
        // Clear races too, not only the read.
        _windows.WaitForInputProcessed(_window);

        return _inspector.Text(_window, box).Value ?? string.Empty;
    }

    /// <summary>The control: the first drain of a process works.</summary>
    /// <remarks>
    /// This is the original measurement, reproduced. It ran five times from five
    /// fresh starts and read back 52 of 52 every time, which is why
    /// <c>WaitForInputIdle</c> was chosen over the two alternatives.
    /// </remarks>
    [Test, Order(1)]
    public void TheFirstDrainAgainstAFreshProcess_ReadsBackEverything()
    {
        TypeThenDrainThenRead().ShouldBe(TheText);
    }

    /// <summary>And every drain after it.</summary>
    /// <remarks>
    /// <b>The question the original measurement never asked.</b> A suite run
    /// types into one long-lived application hundreds of times; if only the first
    /// wait is real, every read after it races, and which tests lose the race
    /// changes from run to run with no code changing — which is exactly what the
    /// guest shows.
    /// </remarks>
    [Test, Order(2)]
    public void EveryLaterDrainAgainstTheSameProcess_ReadsBackEverythingToo()
    {
        List<string> shortReads = [];

        for (int attempt = 1; attempt <= Iterations; attempt++)
        {
            string read = TypeThenDrainThenRead();
            if (!string.Equals(read, TheText, StringComparison.Ordinal))
            {
                shortReads.Add($"attempt {attempt}: {read.Length}/{TheText.Length} chars");
            }
        }

        // The count, named. "Some attempt failed" would not distinguish one
        // unlucky read from the drain being inert for every call after the first.
        shortReads.ShouldBeEmpty(
            $"{shortReads.Count} of {Iterations} reads raced the typing they were meant to wait for");
    }
}

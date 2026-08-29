using System;
using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Raising a window to the foreground.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measured defect these exist for.</b> Across seven full compatibility
/// runs, the count of "NOT RAISED" transcript lines predicted the
/// <c>SendKeys_*</c> failures exactly — 2 in every passing run, 23 in every
/// failing one, never a value between. 22 of the 23 land inside a single minute,
/// so it is one cascading event rather than 23 independent ones.
/// </para>
/// <para>
/// The mechanism is visible end to end: the raise reports failure, the driver
/// types into whatever holds the foreground instead, the test reads back an
/// empty string, and every request answers 200. That is the exact defect this
/// project exists to fix, in its own code.
/// </para>
/// <para>
/// <b>Integration rather than unit, because <c>WindowLocator</c> talks to Win32
/// directly.</b> These read the real foreground and must run under the
/// machine-wide lock — a foreground that another agent is moving would make them
/// meaningless rather than merely flaky.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class ForegroundRaceTests
{
    private WindowLocator _windows = null!;

    [SetUp]
    public void Arrange() => _windows = new WindowLocator();

    /// <summary>A desktop session never tries to raise anything.</summary>
    /// <remarks>
    /// <para>
    /// The desktop can never BE the foreground window, so every raise against it
    /// was doomed — while still running <c>ShowWindow</c>,
    /// <c>BringWindowToTop</c> and <c>SetForegroundWindow</c> against it first.
    /// </para>
    /// <para>
    /// <b>Measured: this fired on every run, passing and failing alike</b> — a
    /// <c>Root</c> session sending Escape to dismiss the Action Center. It was
    /// never a defect, and it was polluting the one metric that does predict the
    /// failure.
    /// </para>
    /// <para>
    /// True rather than false because the caller's real question is "can I act on
    /// this session's target now", and a desktop session's target IS whatever is
    /// in front.
    /// </para>
    /// </remarks>
    [Test]
    public void ADesktopSession_IsAlwaysConsideredRaised()
    {
        _windows.BringToForeground(_windows.DesktopWindow)
            .ShouldBeTrue("a Root session has no window to raise");
    }

    /// <summary>Raising the desktop does not disturb the real foreground.</summary>
    /// <remarks>
    /// <b>The control, and the half that matters.</b> Returning true while still
    /// calling <c>SetForegroundWindow</c> would pass the test above and keep every
    /// side effect — including, plausibly, stealing activation from a shell
    /// surface a test had just opened. This asserts the call is inert.
    /// </remarks>
    [Test]
    public void RaisingTheDesktop_LeavesTheForegroundAlone()
    {
        string before = _windows.DescribeForeground();

        _windows.BringToForeground(_windows.DesktopWindow);

        _windows.DescribeForeground()
            .ShouldBe(before, "raising the desktop must be inert, not merely futile");
    }

    /// <summary>A window that does not exist cannot be raised.</summary>
    [Test]
    public void ADeadHandle_IsNotRaised() =>
        _windows.BringToForeground(0x00DEAD00).ShouldBeFalse();

    /// <summary>A failed raise gives up quickly rather than hanging.</summary>
    /// <remarks>
    /// <para>
    /// <b>The budget bounds a FAILURE, and this is what stops the poll becoming
    /// a wait.</b> The whole objection to polling is that it might cost time on
    /// every call; the answer is that it returns the instant the condition holds
    /// and only ever spends the budget when the raise was going to fail anyway.
    /// </para>
    /// <para>
    /// A dead handle short-circuits before the poll, so this uses a handle that
    /// EXISTS and cannot be foregrounded — the desktop's own child shell window —
    /// to reach the polling path. Asserted generously: the point is that it is
    /// bounded, not that it hits a particular number.
    /// </para>
    /// </remarks>
    [Test]
    public void AnUnraisableWindow_GivesUpWithinTheBudget()
    {
        nint shell = Win32Probe.ShellWindow();

        if (shell == 0)
        {
            Assert.Ignore("No shell window on this session; nothing unraisable to test against.");
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        _windows.BringToForeground(shell);
        elapsed.Stop();

        elapsed.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(2),
            "the poll bounds a failure; it must not become an unbounded wait");
    }

    /// <summary>The diagnostic names something, whatever holds the foreground.</summary>
    /// <remarks>
    /// <para>
    /// <b>Written because the absence of this line cost four runs of
    /// archaeology.</b> The transcript said a raise failed and stopped there, so
    /// proving the keystrokes were landing in another application meant
    /// cross-referencing TRX timings against request logs.
    /// </para>
    /// <para>
    /// Asserted loosely on purpose: WHICH window is in front during a test run is
    /// not this test's business, and pinning it would make the test about the
    /// desktop rather than about the driver. What must hold is that the answer is
    /// never empty — an empty diagnostic is the thing being fixed.
    /// </para>
    /// </remarks>
    [Test]
    public void TheForegroundDescription_IsNeverEmpty()
    {
        string described = _windows.DescribeForeground();

        described.ShouldNotBeNullOrWhiteSpace();

        // Either a real window - which carries a handle - or the honest word for
        // the transient state where nothing holds the foreground at all.
        (described.Contains("hwnd", StringComparison.Ordinal) || described == "nothing")
            .ShouldBeTrue($"unexpected shape: {described}");
    }
}

/// <summary>A window that exists and cannot be foregrounded.</summary>
/// <remarks>
/// The shell's desktop window (<c>Progman</c>) is present on any interactive
/// session and refuses activation, which is exactly the polling path these tests
/// need to reach. Declared here rather than in <c>Win32</c> because nothing in
/// the driver needs it.
/// </remarks>
internal static class Win32Probe
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    public static nint ShellWindow() => GetShellWindow();
}

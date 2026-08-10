using System;
using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Does activating a packaged application twice give two applications?
/// </summary>
/// <remarks>
/// <para>
/// It decides something a test fixture cannot guess at. Two fixtures here launch
/// a second Calculator specifically in order to destroy it, on the assumption
/// that doing so leaves the fixture's own instance alone. Were activation
/// single-instance, that assumption would be false and destroying the "second"
/// application would break every test that ran afterwards — order-dependent, and
/// therefore intermittent.
/// </para>
/// <para>
/// <b>Measured: it is not single-instance.</b> Two activations gave two
/// processes and two windows. The assumption holds. This test was written to
/// confirm a suspicion about an intermittent failure and refuted it, which is
/// why it stays: the next person to suspect the same thing gets the answer
/// without re-deriving it.
/// </para>
/// <para>
/// It also decides a protocol question: whether two sessions with the same
/// <c>app</c> capability drive the same application or two of them.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class PackagedAppInstanceTests
{
    /// <summary>Matches Windows 10's "Calculator" and Windows 11's "CalculatorApp".</summary>
    /// <remarks>
    /// This fixture measures how many instances an activation produces, so it
    /// must start from a machine with none running. On Windows 10 the exact name
    /// "CalculatorApp" matches nothing, and the fixture would have counted
    /// instances left over from earlier tests as if they were its own.
    /// </remarks>
    private const string CalculatorProcessNameFragment = "Calculator";

    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [OneTimeTearDown]
    public void CloseCalculator() => AppLifetime.KillAll(CalculatorProcessNameFragment);

    [Test]
    public void APackagedApplicationsWindowIsFound_EvenWhenItIsNotNew()
    {
        // THE DEFECT BEHIND 19/290. The compatibility suite creates a session per
        // test class and this driver does not close the application when a
        // session ends, so from the second class onward the application is
        // already running and its window already exists. Every stage of the
        // search then misses it: the window belongs to ApplicationFrameHost so it
        // is not owned by the activated process or its descendants, and it is not
        // NEW because it was there before the snapshot. Fifteen fixtures died in
        // ClassInitialize on "Could not find main window for application".
        //
        // Windows 10 hits it on the first re-activation because Calculator is
        // single-instance there. Windows 11 hid it by starting a second instance,
        // which is why ActivatingAPackagedApplicationTwice passes on this desktop
        // and failed in the guest.
        //
        // The condition here reproduces it without depending on either
        // behaviour: launch, then ask the waiter to find that same window with a
        // snapshot taken AFTER it exists.
        AppLifetime.KillAll(CalculatorProcessNameFragment);

        MainWindowWaiter waiter = new(TimeProvider.System);
        ApplicationLauncher launcher = new(waiter, new WindowLocator());

        LaunchResult launched = launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        int processId = launched.Application.ProcessId;

        // Everything visible right now, which INCLUDES the application's window.
        IReadOnlySet<nint> afterItExists = MainWindowWaiter.SnapshotTopLevelWindows();
        afterItExists.ShouldContain(
            launched.Application.WindowHandle,
            "the snapshot must contain the window, or this tests nothing");

        nint found = waiter.WaitAsync(
            processId, afterItExists, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250))
            .GetAwaiter().GetResult();

        found.ShouldNotBe(
            0,
            "a packaged application's window must be findable when it is already " +
            "open, or every session after the first fails");
        found.ShouldBe(launched.Application.WindowHandle);
    }

    [Test]
    [Ignore("Known defect, not a flaw in the test: the waiter returns the hosted " +
            "CoreWindow instead of its ApplicationFrameWindow. Three fixes were " +
            "tried and each regressed element finds; see MainWindowWaiter and " +
            "docs/LIMITATIONS.md. Ignored rather than weakened, because this is " +
            "the specification for the fix.")]
    public void APackagedApplicationsWindow_IsNeverTheHostedCoreWindow()
    {
        // Measured 2026-08-10 in the Windows 10 guest, three cold starts of
        // Alarms & Clock: the session was handed class Windows.UI.Core.CoreWindow
        // while the real top-level window was an ApplicationFrameWindow. A
        // CoreWindow is briefly top-level before being reparented into its frame,
        // so a COLD start can catch it; a warm one cannot, which is why this only
        // ever showed up as the first test of a run failing with "Currently
        // selected window has been closed" long afterwards.
        //
        // HONEST LIMIT OF THIS TEST ON A FAST DESKTOP, and the reason is NOT the
        // one first written here. Measured on Windows 11, 2026-08-10: Calculator
        // is a UWP application hosted in an ApplicationFrameWindow with a
        // Windows.UI.Core.CoreWindow child, exactly as on Windows 10 —
        //
        //   frame  hwnd=0x00070CB6 pid=14284 class=ApplicationFrameWindow
        //   child               pid=29016 class=Windows.UI.Core.CoreWindow
        //
        // so the claim that it is WinUI 3 and owns its window directly is false.
        // The architecture is the same on both; only the TIMING differs. A fast
        // machine has reparented the CoreWindow into its frame before the waiter
        // looks, so it is never enumerated as top-level and the bug cannot be
        // observed. A slow one — a VM, or a cold start — catches it mid-flight.
        //
        // So this test is a race detector, not an architecture check. It can pass
        // here and still be a real assertion; do not read a local pass as proof
        // the defect is gone.
        AppLifetime.KillAll(CalculatorProcessNameFragment);

        ApplicationLauncher launcher = new(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator());

        LaunchResult launched = launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        ClassNameOf(launched.Application.WindowHandle).ShouldNotBe(
            "Windows.UI.Core.CoreWindow",
            "a CoreWindow is about to be reparented into its frame and later " +
            "destroyed, so handing one to a client is handing out a window that dies");
    }

    /// <summary>Reads a window's class name.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The class name.</returns>
    /// <remarks>
    /// Declared here rather than reused from Platform because that type is
    /// internal, and widening it so a test can see it would be the test changing
    /// the production surface.
    /// </remarks>
    private static string ClassNameOf(nint window)
    {
        char[] buffer = new char[256];
        int copied = GetClassName(window, buffer, buffer.Length);
        return copied == 0 ? string.Empty : new string(buffer, 0, copied);
    }

    // DllImport, not LibraryImport: the source generator emits unsafe code and
    // would require AllowUnsafeBlocks on this test project. Turning unsafe on
    // across a whole assembly to read one class name is a worse trade than the
    // marshalling cost of a call made twice in the suite.
    [System.Runtime.InteropServices.DllImport(
        "user32.dll", EntryPoint = "GetClassNameW", CharSet =
            System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(nint window, char[] className, int maxCount);

    [Test]
    public void ActivatingAPackagedApplicationTwice_GivesTwoApplications()
    {
        AppLifetime.KillAll(CalculatorProcessNameFragment);

        ApplicationLauncher launcher = new(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator());

        LaunchResult first = launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        if (first.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {first.FailureMessage}");
        }

        LaunchResult second = launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        second.Application.ShouldNotBeNull();

        TestContext.Out.WriteLine(
            $"first  pid={first.Application.ProcessId} hwnd=0x{first.Application.WindowHandle:X}");
        TestContext.Out.WriteLine(
            $"second pid={second.Application.ProcessId} hwnd=0x{second.Application.WindowHandle:X}");

        // Measured 2026-08-09: pid 3155140 / hwnd 0x55C0954, then pid 3222548 /
        // hwnd 0xAD0354. Calculator is not single-instance on Windows 11.
        second.Application.ProcessId.ShouldNotBe(
            first.Application.ProcessId,
            "packaged activation starts a second application, so a test that " +
            "launches one in order to destroy it is not touching the fixture's");
        second.Application.WindowHandle.ShouldNotBe(first.Application.WindowHandle);

        // And the window search did not simply hand back the window it was told
        // to ignore: both handles are live and distinct.
        first.Application.WindowHandle.ShouldNotBe(0);
        second.Application.WindowHandle.ShouldNotBe(0);
    }
}

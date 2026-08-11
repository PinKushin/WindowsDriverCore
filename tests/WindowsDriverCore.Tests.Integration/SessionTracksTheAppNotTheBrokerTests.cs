using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The process a session tracks must be the application, never
/// <c>ApplicationFrameHost</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a safety test, not a correctness nicety.</b> A packaged
/// application's window is an <c>ApplicationFrameWindow</c> owned by
/// <c>ApplicationFrameHost</c> — a broker shared by every UWP window on the
/// machine — while the application itself owns only the
/// <c>Windows.UI.Core.CoreWindow</c> inside it. Measured on the Windows 10 guest,
/// 2026-08-11: frame pid <b>3704</b> (ApplicationFrameHost) against app pid
/// <b>10832</b> (CalculatorApp), for the same window.
/// </para>
/// <para>
/// <c>ApplicationLauncher</c> derives the tracked process from the window with
/// <c>GetOwningProcessId</c>. That is right while the session's window is the
/// CoreWindow and <b>inverted</b> the moment it becomes the frame, which is what
/// WinAppDriver actually hands back and what this driver should therefore move
/// to. Three consequences, in ascending order of seriousness:
/// </para>
/// <list type="number">
/// <item><description>Two activations of one packaged application both report
/// pid 3704, so they are indistinguishable.</description></item>
/// <item><description><c>WaitForInputProcessed</c> calls <c>WaitForInputIdle</c>
/// on the broker rather than the app. A broker hosting many windows need never be
/// idle — the suspected cause of two integration tests hanging at two
/// minutes.</description></item>
/// <item><description><c>DELETE /session</c> terminates the tracked process. Aimed
/// at the broker, that closes <b>every UWP window on the machine</b>.</description></item>
/// </list>
/// <para>
/// So this test exists to fail loudly the moment the session's window becomes the
/// frame without the process lookup being decoupled from it. It passes today and
/// is not redundant: it is the guard that makes the frame-rooting change safe to
/// attempt.
/// </para>
/// <para>
/// Written in C# rather than as a PowerShell probe deliberately. Four successive
/// probes of this same question failed inside their own scripting layer —
/// <c>IUIAutomation</c> has no <c>IDispatch</c> so late binding cannot call it,
/// PowerShell's XML adapter shadows <c>.Name</c> with the <c>Name</c> attribute,
/// and enum callbacks lose scope. A measurement is only as good as the instrument.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class SessionTracksTheAppNotTheBrokerTests
{
    private const string BrokerProcess = "ApplicationFrameHost";
    private const string PackagedApplication =
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [Test]
    public void APackagedSession_TracksTheApplication_NotApplicationFrameHost()
    {
        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(PackagedApplication, null, null));

        if (launched.Application is null)
        {
            Assert.Fail($"The packaged application would not launch: {launched.FailureMessage}");
            return;
        }

        int tracked = launched.Application.ProcessId;

        try
        {
            int[] brokers = Process.GetProcessesByName(BrokerProcess)
                .Select(process => process.Id)
                .ToArray();

            // The condition must exist, or this test cannot fail for the right
            // reason: with no broker running, "not the broker" is vacuous.
            brokers.ShouldNotBeEmpty(
                "a packaged application is hosted, so ApplicationFrameHost must be running");

            brokers.ShouldNotContain(
                tracked,
                "terminating this session would close every UWP window on the machine");

            // And the positive half. "Not the broker" alone is satisfied by any
            // wrong answer, including zero or an unrelated process.
            Process tracked_ = Process.GetProcessById(tracked);
            tracked_.ProcessName.ShouldBe(
                "CalculatorApp",
                "the tracked process must be the application itself");
        }
        finally
        {
            new ApplicationTerminator().Terminate(tracked);
            AppLifetime.KillAll("CalculatorApp");
        }
    }


    [Test]
    public void RE_ATTACHING_ToARunningPackagedApp_StillTracksTheApplication()
    {
        // **The condition that matters, and the one the first test misses.**
        //
        // MEASURED 2026-08-11: on a COLD launch the frame does not exist yet -
        // the ownership stage answers at ~60 ms with the CoreWindow and the frame
        // appears at ~314 ms - so FindFrameWindowHosting returns zero and never
        // runs. Mutating the waiter to prefer the frame therefore changed nothing
        // on a cold launch, and the first test could not tell.
        //
        // Re-attach is different: the application is already running, its
        // CoreWindow has been reparented into the frame and is no longer
        // top-level, so the ownership stages miss and the FRAME is what gets
        // found. That is also what the compatibility suite does from its second
        // test class onward, because it creates a session per class and this
        // driver does not close the application when a session ends.
        ApplicationLauncher launcher = new(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator());

        LaunchResult first = launcher.Launch(
            new ApplicationTarget(PackagedApplication, null, null));

        if (first.Application is null)
        {
            Assert.Fail($"The packaged application would not launch: {first.FailureMessage}");
            return;
        }

        try
        {
            // Synchronised on the frame existing, which is the condition under
            // test - not on a delay.
            bool framed = SpinWait.SpinUntil(
                () => Process.GetProcessesByName("CalculatorApp").Length > 0 &&
                      new WindowLocator().Exists(first.Application.WindowHandle),
                TimeSpan.FromSeconds(10));

            framed.ShouldBeTrue("the application must be up before re-attaching to it");

            LaunchResult second = launcher.Launch(
                new ApplicationTarget(PackagedApplication, null, null));

            second.Application.ShouldNotBeNull(
                $"re-attach failed outright: {second.FailureMessage}");

            // **The precondition, asserted rather than assumed.** This test is
            // only meaningful if re-attach actually resolved the FRAME - if the
            // ownership stage still answered with something the app owns, the
            // broker can never appear and the test passes vacuously. Reporting
            // the class makes a vacuous pass impossible to mistake for a real one.
            string secondClass = ClassNameOf(second.Application.WindowHandle);
            TestContext.Out.WriteLine($"re-attached window class = {secondClass}");

            if (secondClass != "ApplicationFrameWindow")
            {
                // NOT a pass. MEASURED on the Windows 11 host, 2026-08-11:
                // re-attach resolved Windows.UI.Core.CoreWindow, so the frame
                // path was never taken and the broker could not appear - the
                // assertions below would have been satisfied by doing nothing.
                //
                // This contradicts an earlier reading on the same host, where the
                // application owned no top-level window 313 ms after launch. Two
                // measurements of one thing disagree, so the CoreWindow's
                // lifetime here is unsettled and nothing is built on either.
                Assert.Inconclusive(
                    $"This host re-attached to '{secondClass}', not the frame, so the " +
                    "broker hazard is not reachable here and this test proves nothing. " +
                    "Run it where re-attach resolves ApplicationFrameWindow.");
            }

            int tracked = second.Application.ProcessId;

            int[] brokers = Process.GetProcessesByName(BrokerProcess)
                .Select(process => process.Id)
                .ToArray();

            brokers.ShouldNotBeEmpty(
                "a packaged application is hosted, so ApplicationFrameHost must be running");

            brokers.ShouldNotContain(
                tracked,
                "DELETE /session on this would terminate the broker and close every " +
                "UWP window on the machine");

            Process.GetProcessById(tracked).ProcessName.ShouldBe("CalculatorApp");
        }
        finally
        {
            AppLifetime.KillAll("CalculatorApp");
        }
    }

    [Test]
    public void AClassicSession_TracksItsOwnProcess()
    {
        // The control, and the case that is never in doubt: a plain Win32
        // application creates and owns its own window, so there is no broker in
        // the path at all. It is here so a change that broke process tracking
        // generally could not hide behind the packaged case being special.
        string? found = TestApp.Path;
        if (found is null)
        {
            Assert.Ignore("The WPF test subject has not been built.");
            return;
        }

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(found, null, null));

        try
        {
            launched.Application.ShouldNotBeNull();

            Process tracked = Process.GetProcessById(launched.Application.ProcessId);
            tracked.ProcessName.ShouldBe(TestApp.ProcessName);
        }
        finally
        {
            AppLifetime.KillAll(TestApp.ProcessName);
        }
    }

    private static string ClassNameOf(nint window)
    {
        char[] buffer = new char[256];
        int length = NativeMethods.GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport(
            "user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int GetClassName(
            nint window,
            [System.Runtime.InteropServices.Out] char[] className,
            int maxCount);
    }
}

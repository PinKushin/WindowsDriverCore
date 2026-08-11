using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Waiting for dispatched input must wait on the application, not on the broker.
/// </summary>
/// <remarks>
/// <para>
/// <b>This regressed silently and nothing failed.</b> <c>WaitForInputProcessed</c>
/// opened the process from <c>GetWindowThreadProcessId</c> — the window's
/// <i>owner</i>. That was correct while a session's window was the
/// <c>CoreWindow</c>, and became wrong the moment it became the
/// <c>ApplicationFrameWindow</c>, whose owner is <c>ApplicationFrameHost</c>: a
/// broker shared by every UWP window on the machine.
/// </para>
/// <para>
/// The failure mode is the dangerous one. <c>WaitForInputIdle</c> on the broker
/// returns promptly, so the drain kept reporting success, no test failed on the
/// wait itself, and the input it existed to wait for went on arriving late. A
/// wait that always returns is indistinguishable from no wait at all — except in
/// the tests that quietly keep failing elsewhere.
/// </para>
/// <para>
/// So this asserts the two questions genuinely differ for a packaged session.
/// Without that, any future code reaching for the owning process would look
/// reasonable and be wrong in exactly the same way.
/// </para>
/// <para>
/// <b>What it does NOT verify, stated plainly: which process the drain actually
/// opens.</b> Mutating <c>WaitForInputProcessed</c> back to the owning process
/// leaves this green, because waiting on the broker also returns true — which is
/// precisely the property that made the original regression invisible. Making it
/// sensitive needs the chosen process to be observable, and exposing it purely
/// for a test would put a member on the public surface that nothing else wants.
/// </para>
/// <para>
/// The behavioural check therefore lives in the compatibility suite rather than
/// here, and this fixture is a guard on the <i>distinction</i>: it fails the day
/// the two process ids stop differing, which is the assumption every caller of
/// either one is relying on.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class TheDrainWaitsOnTheAppTests
{
    private const string BrokerProcess = "ApplicationFrameHost";
    private const string PackagedApplication =
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [Test]
    public void ForAPackagedSession_TheOwningAndHostedProcessesDiffer()
    {
        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(PackagedApplication, null, null));

        if (launched.Application is null)
        {
            Assert.Fail($"The packaged application would not launch: {launched.FailureMessage}");
            return;
        }

        try
        {
            WindowLocator windows = new();
            nint window = launched.Application.WindowHandle;

            int owner = windows.GetOwningProcessId(window);
            int hosted = windows.GetHostedProcessId(window);

            // The condition. If these ever stop differing the fixture is no longer
            // measuring anything, and saying so is better than passing quietly.
            owner.ShouldNotBe(
                hosted,
                "a packaged session's window is owned by the broker and hosts the application; " +
                "if they are equal this test cannot distinguish the two and must be revisited");

            Process.GetProcessesByName(BrokerProcess).Select(process => process.Id)
                .ShouldContain(owner, "the window's owner is ApplicationFrameHost");

            using Process hostedProcess = Process.GetProcessById(hosted);
            hostedProcess.ProcessName.ShouldBe(
                "CalculatorApp", "and the hosted process is the application itself");

            // And the drain must succeed against the application. A false here
            // means it could not open the process it intends to wait on.
            windows.WaitForInputProcessed(window).ShouldBeTrue(
                "the drain must be able to wait on the hosted application");
        }
        finally
        {
            AppLifetime.KillAll("CalculatorApp");
        }
    }
}

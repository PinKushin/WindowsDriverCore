using System;
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
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [OneTimeTearDown]
    public void CloseCalculator() => AppLifetime.KillAll("CalculatorApp");

    [Test]
    public void ActivatingAPackagedApplicationTwice_GivesTwoApplications()
    {
        AppLifetime.KillAll("CalculatorApp");

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

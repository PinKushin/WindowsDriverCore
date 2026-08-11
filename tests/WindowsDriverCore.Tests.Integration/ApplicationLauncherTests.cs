using System;
using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The launcher against real applications.
///
/// This is the only place <see cref="ApplicationLauncher"/> and
/// <see cref="MainWindowWaiter"/> are exercised: the protocol tests substitute
/// the interface so session creation can be tested without a desktop, which
/// means everything below the seam has no coverage anywhere else.
///
/// Needs a real desktop session. Skipped rather than failed when the target
/// application is not installed, because a missing Store app is a fact about the
/// machine and not a defect in the driver.
/// </summary>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class ApplicationLauncherTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string NotepadPath = @"C:\Windows\System32\notepad.exe";

    private ApplicationLauncher _launcher = null!;
    private WindowLocator _windows = null!;

    [SetUp]
    public void CreateLauncher()
    {
        _windows = new WindowLocator();
        _launcher = new ApplicationLauncher(new MainWindowWaiter(TimeProvider.System), _windows);
    }

    private static void KillIfRunning(string processName)
    {
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already gone between the enumeration and the kill.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [Test]
    public void Launch_PackagedApplication_ReturnsAWindowOwnedByALiveProcess()
    {
        KillIfRunning("CalculatorApp");

        LaunchResult result = _launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));

        if (result.FailureMessage is not null)
        {
            Assert.Ignore($"Calculator is not available on this machine: {result.FailureMessage}");
        }

        try
        {
            result.Application.ShouldNotBeNull();

            // The window is real and currently exists — not a handle left over
            // from something that has already closed.
            _windows.Exists(result.Application.WindowHandle).ShouldBeTrue();

            // The process id must be the one that OWNS the window, not the one
            // activation returned. For a packaged app those differ: activation
            // reports a broker, and every later process-scoped operation would
            // address the wrong process. Asserting only "non-zero" would pass on
            // the broker id and hide exactly that bug.
            result.Application.ProcessId
                .ShouldBe(_windows.GetHostedProcessId(result.Application.WindowHandle));
            result.Application.ProcessId.ShouldBeGreaterThan(0);
        }
        finally
        {
            KillIfRunning("CalculatorApp");
        }
    }

    [Test]
    public void Launch_ClassicApplication_ReturnsAWindowOwnedByThatProcess()
    {
        KillIfRunning("Notepad");

        LaunchResult result = _launcher.Launch(new ApplicationTarget(NotepadPath, null, null));

        if (result.FailureMessage is not null)
        {
            Assert.Ignore($"Notepad is not available on this machine: {result.FailureMessage}");
        }

        try
        {
            result.Application.ShouldNotBeNull();
            _windows.Exists(result.Application.WindowHandle).ShouldBeTrue();
            result.Application.ProcessId
                .ShouldBe(_windows.GetHostedProcessId(result.Application.WindowHandle));
        }
        finally
        {
            KillIfRunning("Notepad");
        }
    }

    [Test]
    public void Launch_MissingExecutable_FailsWithTheProtocolMessage()
    {
        LaunchResult result = _launcher.Launch(
            new ApplicationTarget(@"C:\this\does\not\exist.exe", null, null));

        result.Application.ShouldBeNull();

        // The exact string real WinAppDriver returns, because the compatibility
        // suite asserts on the message rather than the status alone.
        result.FailureMessage.ShouldBe("The system cannot find the file specified");
    }

    [Test]
    public void Launch_UnknownPackage_FailsRatherThanHanging()
    {
        // An AUMID that parses but names nothing installed. The failure must come
        // from activation rejecting it, not from the window wait timing out ten
        // seconds later — so this also pins that COMException is handled.
        Stopwatch elapsed = Stopwatch.StartNew();

        LaunchResult result = _launcher.Launch(
            new ApplicationTarget("NotAPackage_00000000000!App", null, null));

        elapsed.Stop();

        result.Application.ShouldBeNull();
        result.FailureMessage.ShouldBe("The system cannot find the file specified");

        // Well inside the ten-second window timeout. If activation silently
        // succeeded and we fell through to waiting, this would take much longer
        // and the message would be about a missing window instead.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Launch_InvalidWorkingDirectory_FailsBeforeStartingAnything()
    {
        KillIfRunning("Notepad");

        LaunchResult result = _launcher.Launch(
            new ApplicationTarget(NotepadPath, null, @"C:\no\such\directory"));

        result.Application.ShouldBeNull();
        result.FailureMessage.ShouldBe("The directory name is invalid");

        // The bystander: rejecting the request must not have started the
        // application anyway. Without this, "validated first" and "started then
        // failed" look identical from the result alone.
        Process.GetProcessesByName("Notepad").ShouldBeEmpty();
    }

    [TestCase("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [TestCase(@"C:\Windows\System32\notepad.exe", false)]
    [TestCase("notepad.exe", false)]
    [TestCase(@"C:\tools\build!final\app.exe", false)]
    public void IsPackagedApplication_DistinguishesAumidsFromPaths(string app, bool expected)
    {
        // The last case is why the rooted-path check exists: a path may contain
        // '!' legitimately, and treating it as an AUMID would send it to COM
        // activation and fail naming the wrong thing.
        ApplicationLauncher.IsPackagedApplication(app).ShouldBe(expected);
    }
}

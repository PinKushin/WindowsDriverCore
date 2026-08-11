using System;
using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A process still showing another window is not this session's to end.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ordinary case on Windows 11, not an exotic one.</b> Notepad — and Store
/// applications generally — are single-instance and multi-window, so a session
/// that launched one and a person who already had one open share a process.
/// Terminating by process id closes their window too, and the packaged host then
/// treats the death as a crash and restores the session it just lost. Observed
/// 2026-08-11 as a Notepad left on the desktop showing "Not a valid file name."
/// </para>
/// <para>
/// <b>The driver already knew the principle and had it scoped to one process.</b>
/// <c>explorer.exe</c> got this exact treatment — close the window, never touch
/// the process — because the consequence there was loud enough to notice. The
/// rule is not about the shell; it is about whether anything else is still using
/// the application.
/// </para>
/// <para>
/// <b>Asserted as a DECISION, like <c>IsTheDesktopShell</c> beside it.</b> The
/// end-to-end version has to kill something to find out it was wrong.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class ASecondWindowMeansSomebodyIsUsingItTests
{
    /// <summary>
    /// A process whose only visible window is the one being closed has nothing
    /// left.
    /// </summary>
    /// <remarks>
    /// THE CONTROL. Without it, "always says another window remains" and "says so
    /// when one does" predict the same observation on the test below — and the
    /// first would switch termination off entirely while every assertion still
    /// passed.
    /// </remarks>
    [Test]
    public void TheOnlyWindow_LeavesNothingBehind()
    {
        using Subject subject = Subject.Started();

        ApplicationTerminator
            .HasAnotherVisibleWindow(subject.ProcessId, subject.Window)
            .ShouldBeFalse(
                "its only visible window is the one being closed, so terminating " +
                "the process takes nothing that belongs to anybody else");
    }

    /// <summary>
    /// A second visible window in the same process means the process is in use.
    /// </summary>
    [Test]
    public void ASecondWindow_IsSeen()
    {
        using Subject subject = Subject.Started();

        // Excluding a window this process does not own leaves its real window as
        // "another" one — the same question the terminator asks after closing a
        // session window, with a different window named.
        ApplicationTerminator
            .HasAnotherVisibleWindow(subject.ProcessId, excluding: 0)
            .ShouldBeTrue("the process is showing a window that is not the excluded one");
    }

    [Test]
    public void AProcessThatIsNotThere_HasNothing()
    {
        ApplicationTerminator.HasAnotherVisibleWindow(0, 0).ShouldBeFalse();
        ApplicationTerminator.HasAnotherVisibleWindow(-1, 0).ShouldBeFalse();
    }

    /// <summary>The project's own WPF application, launched and awaited.</summary>
    /// <remarks>
    /// Ours rather than Notepad, because the subject has to be one whose window
    /// count is known. Notepad is the application this is ABOUT and is therefore
    /// the worst possible instrument for measuring it: its instancing is the
    /// variable under test.
    /// </remarks>
    private sealed class Subject : IDisposable
    {
        private readonly WindowLocator _windows = new();

        private Subject(int processId, nint window)
        {
            ProcessId = processId;
            Window = window;
        }

        public int ProcessId { get; }

        public nint Window { get; }

        public static Subject Started()
        {
            ApplicationLauncher launcher = new(
                new MainWindowWaiter(TimeProvider.System), new WindowLocator());

            string? path = TestApp.Path;
            if (path is null)
            {
                Assert.Ignore("The test application has not been built.");
            }

            LaunchResult result = launcher.Launch(new ApplicationTarget(path, null, null));

            if (result.FailureMessage is not null)
            {
                Assert.Ignore($"The test application could not be started: {result.FailureMessage}");
            }

            result.Application.ShouldNotBeNull();
            return new Subject(result.Application.ProcessId, result.Application.WindowHandle);
        }

        public void Dispose()
        {
            if (_windows.Exists(Window))
            {
                _windows.Close(Window);
                _windows.WaitUntilGone(Window);
            }

            foreach (Process process in Process.GetProcessesByName(TestApp.ProcessName))
            {
                try
                {
                    if (process.Id == ProcessId && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Exited between the enumeration and the kill.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}

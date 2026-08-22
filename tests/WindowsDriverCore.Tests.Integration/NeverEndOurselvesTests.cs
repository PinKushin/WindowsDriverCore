using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A session teardown must never end the driver's own process.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED 2026-08-22, and it destroyed an entire compatibility run.</b> One
/// run scored <b>41/290</b> where the same commit scored 261 on a re-run.
/// Everything after the second minute failed with
/// <c>"No connection could be made because the target machine actively refused
/// it 127.0.0.1:4723"</c> — the driver was gone.
/// </para>
/// <para>
/// The transcript names the mechanism. A classic launch adopted the wrong
/// window:
/// </para>
/// <code>
/// launch 'C:\Windows\System32\notepad.exe' -> pid 3872 window 0x2307F0 (ConsoleWindowClass) 21.6 ms
/// </code>
/// <para>
/// <c>ConsoleWindowClass</c> — a console, not Notepad. <c>MainWindowWaiter</c>'s
/// last resort is "any top-level window that did not exist before the launch",
/// documented there as a guess a busy desktop can get wrong, and the driver runs
/// with a console of its own. The session then owned that window, and
/// <c>DELETE /session</c> terminates what a session owns. The driver killed
/// itself.
/// </para>
/// <para>
/// <b>No crash report was written</b>, which is itself evidence: the managed
/// crash handler catches unhandled exceptions, and this process was not throwing
/// — it was being terminated.
/// </para>
/// <para>
/// Two guards, and the second is the one that matters. The waiter must not adopt
/// our own window, and the terminator must refuse our own process even if it
/// somehow does. Same shape as <see cref="ApplicationTerminator.IsTheDesktopShell"/>
/// beside it, which exists because ending the shell takes the desktop with it —
/// ending ourselves takes every session on the machine.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class NeverEndOurselvesTests
{
    [Test]
    public void OurOwnProcess_IsRecognised()
    {
        using Process self = Process.GetCurrentProcess();

        ApplicationTerminator.IsThisProcess(self.Id).ShouldBeTrue(
            "ending this process would take the driver and every live session with it");
    }

    [Test]
    public void AnotherProcess_IsNot()
    {
        // The control. A predicate returning true for everything would pass the
        // test above and make every session teardown a silent no-op - which
        // would look like applications failing to close, a long way from a wrong
        // self check.
        using Process shell = Process.GetCurrentProcess();

        // The desktop shell is a real, live process that is definitely not us.
        int other = ShellProcessId();
        if (other == 0 || other == shell.Id)
        {
            Assert.Inconclusive("No distinct second process available to contrast against.");
            return;
        }

        ApplicationTerminator.IsThisProcess(other).ShouldBeFalse(
            "another running process must remain terminable");

        ApplicationTerminator.IsThisProcess(0).ShouldBeFalse("nor is nothing");
        ApplicationTerminator.IsThisProcess(-1).ShouldBeFalse();
    }

    /// <summary>Terminating ourselves is refused outright.</summary>
    /// <remarks>
    /// The end-to-end statement, and the one that would have saved the 41-point
    /// run. It asserts the REFUSAL rather than the predicate, so a guard that is
    /// correct but never consulted still fails here.
    /// </remarks>
    [Test]
    public void TerminatingOurselves_IsRefused_AndWeAreStillRunning()
    {
        using Process self = Process.GetCurrentProcess();

        bool ended = new ApplicationTerminator().Terminate(self.Id, 0);

        ended.ShouldBeFalse("the driver must refuse to end itself");

        // The observation that actually matters. If the guard were missing this
        // line would never run, so its value is in the process still being here
        // to run it.
        Process.GetCurrentProcess().HasExited.ShouldBeFalse();
    }

    private static int ShellProcessId()
    {
        Process[] candidates = Process.GetProcessesByName("explorer");
        try
        {
            return candidates.Length > 0 ? candidates[0].Id : 0;
        }
        finally
        {
            foreach (Process candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }
}

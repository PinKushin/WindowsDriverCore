using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The one process this driver must never end.
/// </summary>
/// <remarks>
/// <para>
/// A File Explorer window belongs to the long-running <c>explorer.exe</c> that
/// draws the desktop, taskbar and Start menu. <c>ApplicationLauncher</c> tracks
/// the window's OWNING process, so a session opened on Explorer tracks the shell
/// — and <c>DELETE /session</c> would call <c>CloseMainWindow</c> on it and then
/// <c>Kill</c> it, taking the whole desktop down with the session.
/// </para>
/// <para>
/// <b>This asserts the DECISION, never the action.</b> The obvious test — end the
/// shell and check the desktop survived — destroys the machine it runs on when it
/// fails, so it is unusable as a test regardless of how well it would demonstrate
/// the property. Splitting the predicate out makes the dangerous half assertable
/// at no risk.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class NeverEndTheDesktopShellTests
{
    [Test]
    public void TheDesktopShell_IsRecognised()
    {
        int shell = ShellProcessIdFromTheDesktop();

        if (shell == 0)
        {
            Assert.Inconclusive("No desktop shell is running, so there is nothing to recognise.");
            return;
        }

        ApplicationTerminator.IsTheDesktopShell(shell).ShouldBeTrue(
            "ending this process would take the desktop, taskbar and Start menu with it");
    }

    [Test]
    public void AnOrdinaryProcess_IsNot()
    {
        // The control. Without it, a predicate returning true for everything
        // would pass the test above and quietly make every session teardown a
        // no-op — the failure would look like applications not closing, which is
        // a long way from a wrong shell check.
        using Process self = Process.GetCurrentProcess();

        ApplicationTerminator.IsTheDesktopShell(self.Id).ShouldBeFalse(
            "the test host is not the shell and must remain terminable");

        ApplicationTerminator.IsTheDesktopShell(0).ShouldBeFalse("nor is nothing");
        ApplicationTerminator.IsTheDesktopShell(-1).ShouldBeFalse();
    }

    private static int ShellProcessIdFromTheDesktop()
    {
        // Resolved independently of the code under test. Asking the terminator
        // which process is the shell and then asserting it recognises that
        // process would be circular.
        nint shellWindow = GetShellWindow();
        if (shellWindow == 0)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(shellWindow, out uint pid);
        return (int)pid;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

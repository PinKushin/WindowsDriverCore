using System;
using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Launching an application whose process exits immediately.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED on the Windows 10 guest, 2026-08-11</b>, through this driver's own
/// HTTP surface:
/// </para>
/// <code>
/// POST /session  app=C:\Windows\System32\explorer.exe
///   -> 500 {"status":13,"message":"The system cannot find the file specified"}
/// POST /session  app=C:\Windows\System32\notepad.exe
///   -> 200, title='Untitled - Notepad'
/// </code>
/// <para>
/// <b>The message was literally true and useless.</b> There is no
/// <c>explorer.exe</c> under the real <c>System32</c> — measured on both systems —
/// and WinAppDriver finds one only because it is a 32-bit process and WOW64
/// redirects it to <c>SysWOW64</c>. So the difference is process architecture, and
/// it blocks nine compatibility-suite tests — <c>CreateSession_SystemApp</c>,
/// <c>CreateSessionWithArguments_SystemApp</c>,
/// <c>CreateSessionWithWorkingDirectoryAndArguments</c>,
/// <c>DeleteSession_SystemApp</c>, <c>SwitchWindows</c>,
/// <c>GetWindowHandles_ModernApp</c> and the Forward/Back system-app pair — and
/// WinAppDriver passes them.
/// </para>
/// <para>
/// <b>Also measured, and a red herring worth recording.</b> <c>explorer.exe</c>
/// starts and then <i>exits within 1.5 seconds</i>, handing the request to the
/// running shell which owns the window that appears; <c>notepad.exe</c> stays
/// alive. That difference is real and is <i>not</i> the cause — the launch never
/// got as far as starting anything. It is kept because it was the obvious
/// suspect and cost a probe to eliminate.
/// </para>
/// <para>
/// <c>LaunchCore</c> still maps three separate causes onto one message —
/// <c>FileNotFoundException</c>, <c>Win32Exception</c> and <c>processId == 0</c> —
/// so the wire cannot say which happened. That is why this took a local
/// reproduction to diagnose at all, and it remains a diagnosability defect.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class LaunchingExplorerTests
{
    private const string Explorer = @"C:\Windows\System32\explorer.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    [Test]
    public void AnApplicationWhoseProcessExitsImmediately_StillGetsASession()
    {
        // The path does NOT exist to a 64-bit process, and that is the point.
        // MEASURED on both the Windows 11 host and the Windows 10 guest:
        //
        //   C:\Windows\System32\explorer.exe   absent
        //   C:\Windows\SysWOW64\explorer.exe   present
        //   C:\Windows\explorer.exe             present
        //
        // WinAppDriver.exe is x86 — measured, PE machine 0x014C — so WOW64
        // redirects its System32 lookups to SysWOW64 and the path resolves for it.
        // A 64-bit driver sees the real System32 and correctly finds nothing.
        // Nine suite tests hardcode this path, so the incompatibility is one of
        // process architecture rather than behaviour.
        System.IO.File.Exists(Explorer).ShouldBeFalse(
            "if this ever becomes true the redirection below is no longer needed");

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(Explorer, null, null));

        try
        {
            launched.Application.ShouldNotBeNull(
                $"explorer.exe exists and WinAppDriver opens a session on it; " +
                $"this driver answered '{launched.FailureMessage}'");

            launched.Application.WindowHandle.ShouldNotBe(
                0, "a session needs a window to drive");
        }
        finally
        {
            // Deliberately NOT killing explorer: the window belongs to the running
            // shell, and ending that process takes the user's desktop with it. The
            // window this opened is a File Explorer window, closed by the shell
            // when the test host goes away.
            if (launched.Application is not null)
            {
                new WindowLocator().Close(launched.Application.WindowHandle);
            }
        }
    }

    [Test]
    public void AnApplicationWhoseProcessSurvives_GetsASession()
    {
        // The control. It isolates "the process exits immediately" as the only
        // difference between the two cases — without it, a launcher broken for
        // every classic application would look like an Explorer quirk.
        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(Notepad, null, null));

        try
        {
            launched.Application.ShouldNotBeNull(launched.FailureMessage);

            using Process notepad = Process.GetProcessById(launched.Application.ProcessId);
            notepad.HasExited.ShouldBeFalse("this one stays alive, unlike explorer.exe");
        }
        finally
        {
            if (launched.Application is not null)
            {
                Support.AppLifetime.KillProcess(launched.Application.ProcessId);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

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

    /// <summary>The process ids currently running under a name.</summary>
    /// <remarks>
    /// Taken before a launch so that afterwards the difference names what the
    /// launch added. Ids rather than <see cref="Process"/> objects: the objects
    /// would have to be disposed, and only the identity is wanted.
    /// </remarks>
    private static HashSet<int> ProcessIdsNamed(string processName)
    {
        HashSet<int> running = [];

        foreach (Process process in Process.GetProcessesByName(processName))
        {
            running.Add(process.Id);
            process.Dispose();
        }

        return running;
    }

    /// <summary>
    /// Ends what a launch added, and nothing that was already running.
    /// </summary>
    /// <param name="processName">The process name to look under.</param>
    /// <param name="before">The ids from before the launch.</param>
    /// <param name="application">
    /// What the launch returned, whose window is closed first. Null when there is
    /// no window — the bystander fixture drives this helper with plain console
    /// processes, and a launch that failed has nothing to close.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>This used to kill every process with the name, and it took the owner's
    /// own Notepad with it</b> — observed 2026-08-11. That is the same class of
    /// mistake as aiming a session teardown at <c>ApplicationFrameHost</c>, which
    /// this project already forbids: a name is not an identity, and a test has no
    /// claim on a window somebody else opened.
    /// </para>
    /// <para>
    /// <b>The difference, rather than the launched id, and that part is not
    /// tidiness.</b> On Windows 11 <c>System32\notepad.exe</c> redirects to the
    /// packaged Notepad, which is single-instance and session-restoring — so the
    /// process that ends up owning the window is frequently NOT the one that was
    /// started, the same intermediate-process-id problem the driver already knows
    /// about from packaged activation. Killing the reported id alone left a
    /// Notepad running that then showed a "Not a valid file name." modal from a
    /// failed session restore.
    /// </para>
    /// <para>
    /// A process that another agent starts under the same name inside this window
    /// would be caught too. The window is the length of one launch, and the
    /// alternative — trusting a parent id across a packaged relaunch — is the
    /// thing that does not work here.
    /// </para>
    /// </remarks>
    private void StopWhatWasStarted(
        string processName, HashSet<int> before, LaunchedApplication? application)
    {
        // THE WINDOW FIRST, because on a single-instance application that is the
        // only thing the launch actually added. Measured 2026-08-11 with a
        // Notepad already open: a run took the process count from 1 to 1 and the
        // window count from 1 to 2, so a difference of process ids saw nothing to
        // clean and left the test's window on the desktop.
        //
        // WM_CLOSE through the same locator the driver uses, so a test tears an
        // application down the way DELETE /session does.
        if (application is not null && _windows.Exists(application.WindowHandle))
        {
            _windows.Close(application.WindowHandle);

            // A close request on an application holding unsaved work raises a
            // modal, and a modal nobody answers sits on a SHARED desktop until
            // the kill below times out. See UnsavedWorkPrompt.
            UnsavedWorkPrompt.DiscardIfAsked(application.WindowHandle);

            _windows.WaitUntilGone(application.WindowHandle);
        }

        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (before.Contains(process.Id))
                {
                    continue;
                }

                // ASKED TO CLOSE BEFORE BEING KILLED, and that ordering is the
                // fix rather than politeness. Killing the packaged Notepad makes
                // it come back: it treats the death as a crash and restores its
                // session under a new pid, which is how a run that "cleaned up"
                // left a Notepad on the desktop at 16:22 showing a "Not a valid
                // file name." modal from the restore it had just attempted.
                // WM_CLOSE is an ordinary exit and nothing is restored.
                //
                // The same verb ApplicationTerminator uses for the same reason,
                // so a test tears an application down the way the driver does.
                if (process.CloseMainWindow())
                {
                    // Same reason as above, for the process the launch added
                    // rather than the window it reported. The handle is read
                    // after the request, because a window that only appears in
                    // response to the close — a save prompt — is the case this
                    // is for.
                    process.Refresh();
                    UnsavedWorkPrompt.DiscardIfAsked(process.MainWindowHandle);

                    process.WaitForExit(5000);
                }

                if (!process.HasExited)
                {
                    // It ignored the request, or never had a window to send it
                    // to. Nothing gentler is left.
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone between the enumeration and the close.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The cleanup ends what a launch added and leaves what was already there.
    /// </summary>
    /// <remarks>
    /// <b>The bystander is the whole experiment.</b> With one subject, "ended the
    /// right process" and "ended every process with that name" predict the same
    /// observation — and the second is what the old helper did, which closed the
    /// owner's own Notepad on 2026-08-11. Notepad cannot play the subject here
    /// because it is single-instance: two launches are one process, so there
    /// would be nothing to tell apart.
    /// </remarks>
    [Test]
    public void StoppingWhatWasStarted_EndsTheNewProcess_AndSparesTheOneAlreadyRunning()
    {
        using Process bystander = StartWaiter();

        HashSet<int> before = ProcessIdsNamed(WaiterName);
        before.ShouldContain(bystander.Id, "the snapshot must include the bystander");

        using Process started = StartWaiter();

        StopWhatWasStarted(WaiterName, before, application: null);

        started.WaitForExit(5000);
        started.HasExited.ShouldBeTrue("the process the launch added must be ended");
        bystander.HasExited.ShouldBeFalse(
            "a process that was already running is not this test's to kill");

        bystander.Kill(entireProcessTree: true);
        bystander.WaitForExit(5000);
    }

    /// <summary>A process that waits rather than exiting, and owns no window.</summary>
    /// <remarks>
    /// <c>cmd /c pause</c> blocks reading its input, so redirecting that input and
    /// never writing to it holds the process open for exactly as long as the test
    /// needs — with no window to steal focus from whatever else is on the desktop.
    /// </remarks>
    private static Process StartWaiter()
    {
        ProcessStartInfo waiting = new()
        {
            FileName = "cmd.exe",
            Arguments = "/c pause",
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        Process? started = Process.Start(waiting);
        started.ShouldNotBeNull();
        return started;
    }

    private const string WaiterName = "cmd";

    /// <summary>
    /// The window a launch produced is gone afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The instrument, corrected.</b> Counting processes was the wrong
    /// measurement and it passed while the defect was in plain sight: measured
    /// 2026-08-11 with a Notepad already open, a run took the process count from
    /// 1 to 1 and the WINDOW count from 1 to 2. Modern Notepad is single-instance
    /// and multi-window, so the thing a launch adds is a window, and a difference
    /// of process ids can never see it.
    /// </para>
    /// <para>
    /// This is the case the owner reported as "neither closed" — both windows
    /// were in one process that the cleanup correctly declined to kill, because
    /// it had been running before the test started.
    /// </para>
    /// </remarks>
    [Test]
    public void Launching_ThenStopping_LeavesNoWindowBehind()
    {
        HashSet<int> before = ProcessIdsNamed("Notepad");

        LaunchResult result = _launcher.Launch(new ApplicationTarget(NotepadPath, null, null));

        if (result.FailureMessage is not null)
        {
            Assert.Ignore($"Notepad is not available on this machine: {result.FailureMessage}");
        }

        result.Application.ShouldNotBeNull();
        nint window = result.Application.WindowHandle;

        StopWhatWasStarted("Notepad", before, result.Application);

        _windows.Exists(window).ShouldBeFalse(
            "the window the launch produced must be gone; a process count cannot " +
            "see it, because Notepad is single-instance and multi-window");
    }

    /// <summary>
    /// An application holding unsaved work is torn down without leaving its
    /// "save changes?" prompt on the desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The condition is the typed character, and without it this test cannot
    /// fail.</b> An untouched Notepad closes on WM_CLOSE with no prompt, so
    /// dismissing the prompt and never producing one predict the same
    /// observation. The text is what makes the two differ.
    /// </para>
    /// <para>
    /// <b>The measurement is the clock, because the end state is the same either
    /// way.</b> The old teardown also ended up with no window and no process — it
    /// got there by letting <c>WaitForExit(5000)</c> expire against a modal
    /// nobody answered and then killing. So "no window afterwards" is blind to
    /// the defect; the five seconds a focus-stealing modal spends on a shared
    /// desktop is the thing being fixed, and elapsed time is what sees it.
    /// </para>
    /// <para>
    /// Measured 2026-08-11 on Windows 11 26200: the prompt is a WinUI
    /// <c>ContentDialog</c> INSIDE the application's own window, not a separate
    /// top-level dialog, so no amount of window enumeration finds it.
    /// </para>
    /// </remarks>
    [Test]
    public void StoppingAnApplicationWithUnsavedWork_LeavesNoPromptAndTakesNoKillWait()
    {
        HashSet<int> before = ProcessIdsNamed("Notepad");

        LaunchResult result = _launcher.Launch(new ApplicationTarget(NotepadPath, null, null));

        if (result.FailureMessage is not null)
        {
            Assert.Ignore($"Notepad is not available on this machine: {result.FailureMessage}");
        }

        result.Application.ShouldNotBeNull();
        nint window = result.Application.WindowHandle;

        TypeSomethingUnsaved(window);

        Stopwatch teardown = Stopwatch.StartNew();
        StopWhatWasStarted("Notepad", before, result.Application);
        teardown.Stop();

        _windows.Exists(window).ShouldBeFalse("the window must be gone");
        ProcessIdsNamed("Notepad").Except(before).ShouldBeEmpty(
            "nothing the launch added may survive, including a restored instance");

        // The prediction that separates the two. Answering the prompt is fast;
        // waiting it out is the five-second WaitForExit and then a kill.
        teardown.Elapsed.ShouldBeLessThan(
            TimeSpan.FromMilliseconds(4000),
            "the save prompt must be answered, not waited out and killed");
    }

    /// <summary>
    /// Puts unsaved content into the window, and proves it landed there.
    /// </summary>
    /// <remarks>
    /// <b>The title check is not decoration.</b> Synthesised keystrokes go to
    /// whatever holds the foreground, so without confirming the modified marker
    /// this helper could type into another agent's window and the test would
    /// still measure a clean teardown — of an application with nothing unsaved.
    /// That is the same class of mistake as an unguarded coordinate click.
    /// </remarks>
    private void TypeSomethingUnsaved(nint window)
    {
        _windows.BringToForeground(window).ShouldBeTrue(
            "typing into a window that is not in front sends the keys elsewhere");
        _windows.WaitForInputProcessed(window);

        new SendInputKeyboard().Type("unsaved").ShouldBeTrue();

        UiSettle.Until(
            () => _windows.GetTitle(window).StartsWith('*'),
            TimeSpan.FromSeconds(10),
            "Notepad to report unsaved changes in its title");
    }

    [Test]
    public void Launch_PackagedApplication_ReturnsAWindowOwnedByALiveProcess()
    {
        HashSet<int> before = ProcessIdsNamed("CalculatorApp");

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
            StopWhatWasStarted("CalculatorApp", before, result.Application);
        }
    }

    [Test]
    public void Launch_ClassicApplication_ReturnsAWindowOwnedByThatProcess()
    {
        HashSet<int> before = ProcessIdsNamed("Notepad");

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
            StopWhatWasStarted("Notepad", before, result.Application);
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
        HashSet<int> before = ProcessIdsNamed("Notepad");

        LaunchResult result = _launcher.Launch(
            new ApplicationTarget(NotepadPath, null, @"C:\no\such\directory"));

        result.Application.ShouldBeNull();
        result.FailureMessage.ShouldBe("The directory name is invalid");

        // The bystander: rejecting the request must not have started the
        // application anyway. Without this, "validated first" and "started then
        // failed" look identical from the result alone.
        //
        // The DIFFERENCE, not the total. Asserting the machine has no Notepad at
        // all made this test a statement about the developer's desktop, and the
        // pre-kill that made it pass is what closed the owner's own window.
        ProcessIdsNamed("Notepad").Except(before).ShouldBeEmpty(
            "rejecting the request must not have started the application anyway");
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

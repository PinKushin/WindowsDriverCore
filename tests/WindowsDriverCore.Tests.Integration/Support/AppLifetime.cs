using System;
using System.Diagnostics;
using NUnit.Framework;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>Starting and stopping applications a fixture owns.</summary>
internal static class AppLifetime
{
    /// <summary>
    /// Kills one application by process id.
    /// </summary>
    /// <remarks>
    /// By id, never by process name. A fixture that shares a launched
    /// application with its tests and kills "CalculatorApp" takes its own
    /// instance down too, and every later test in the fixture then fails with
    /// "no elements found" — a failure that looks like the code under test and
    /// is really test-order coupling.
    /// </remarks>
    internal static void KillProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Exited between lookup and kill.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    /// <summary>Whether a handle is still a window.</summary>
    /// <param name="window">The handle.</param>
    /// <returns><see langword="true"/> while the window exists.</returns>
    internal static bool WindowExists(nint window) => IsWindow(window);

    /// <summary>
    /// Blocks until a window handle stops being a window.
    /// </summary>
    /// <param name="window">The handle.</param>
    /// <remarks>
    /// <para>
    /// <b>Killing a process and observing its window are not the same event.</b>
    /// <c>Process.Kill</c> returning, and even <c>WaitForExit</c>, says the
    /// process is gone; the window and UI Automation's view of it are torn down
    /// afterwards, and how long that takes depends on how busy the machine is.
    /// </para>
    /// <para>
    /// A test that kills an application and immediately asserts UIA agrees is
    /// therefore measuring a condition that becomes true asynchronously. Observed
    /// 2026-08-10 under load from an Appium run: the element still resolved after
    /// the kill.
    /// </para>
    /// <para>
    /// This waits for the <b>manipulation to finish</b>, which is not the same as
    /// retrying a failing assertion. The measurement still happens exactly once,
    /// against a state that has actually been reached.
    /// </para>
    /// </remarks>
    internal static void WaitUntilWindowIsGone(nint window)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (!IsWindow(window))
            {
                return;
            }
        }

        Assert.Fail(
            $"Window 0x{window:X} still exists {elapsed.Elapsed.TotalSeconds:F0}s after its " +
            "process was killed. The application did not shut down.");
    }

    /// <summary>Kills every process with a name, for fixture teardown.</summary>
    /// <param name="processName">The name, without extension.</param>
    /// <summary>Ends every process whose name contains <paramref name="processName"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Substring, not exact, and that is a correctness fix rather than
    /// convenience.</b> Windows 10 names Calculator's process <c>Calculator</c>
    /// and Windows 11 names it <c>CalculatorApp</c>. An exact match on either
    /// one silently does nothing on the other Windows: measured 2026-08-10 in
    /// the Windows 10 guest, where every KillAll("CalculatorApp") in this suite
    /// matched no process at all and applications accumulated for the whole run.
    /// </para>
    /// <para>
    /// The same mistake made the first WinAppDriver quit probe worthless — it
    /// counted CalculatorApp on Windows 10, saw zero before and zero after, and
    /// reported a verdict from a comparison that could not discriminate.
    /// </para>
    /// <para>
    /// Prefer ending a session, or killing a known process id. This exists for
    /// the fixtures that must start from a machine with none of an application
    /// running, which is a question about the machine rather than about a
    /// process this suite owns.
    /// </para>
    /// </remarks>
    internal static void KillAll(string processName)
    {
        Process[] matching = Array.FindAll(
            Process.GetProcesses(),
            candidate => candidate.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase));

        foreach (Process process in matching)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

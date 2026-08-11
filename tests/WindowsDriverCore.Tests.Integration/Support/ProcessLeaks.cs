using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// Notices applications this suite started and did not close.
/// </summary>
/// <remarks>
/// <b>A window left open is a test failure that no test reports.</b> Every
/// fixture can pass while leaving an application running, because nothing
/// asserts on it — the suite is blind to it by construction. Three stray
/// Calculators were once found by a person looking at the desktop, which is not
/// an instrument.
/// </remarks>
internal static class ProcessLeaks
{
    /// <summary>Applications this suite is capable of launching.</summary>
    /// <remarks>
    /// Both Windows 10's <c>Calculator</c> and Windows 11's <c>CalculatorApp</c>,
    /// and <c>Time</c>, which is what Alarms &amp; Clock is called in the process
    /// list.
    /// </remarks>
    private static readonly string[] Launchable =
    [
        "Calculator", "CalculatorApp", "notepad", "Time", "charmap",
        "SystemSettings", "WindowsDriverCore.TestApp.Wpf",
    ];

    private static HashSet<int> _presentBeforeTheRun = [];

    /// <summary>Records what was already running.</summary>
    /// <remarks>
    /// <b>Process IDS, not names.</b> The developer may have Calculator open for
    /// their own reasons, and a suite that cannot tell that instance from its own
    /// has no business killing anything.
    /// </remarks>
    internal static void Snapshot() =>
        _presentBeforeTheRun = [.. Running().Select(process => process.Id)];

    /// <summary>Anything started during the run and still alive, killed on the way out.</summary>
    /// <returns>A description of each leak, empty when there are none.</returns>
    internal static IReadOnlyList<string> TakeLeaks()
    {
        List<Process> leaked = [.. Running().Where(p => !_presentBeforeTheRun.Contains(p.Id))];
        List<string> described = [.. leaked.Select(p => $"{p.ProcessName}({p.Id})")];

        foreach (Process process in leaked)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Gone between listing and killing. The point was to report it.
            }
        }

        return described;
    }

    private static IEnumerable<Process> Running() =>
        Launchable
            .SelectMany(Process.GetProcessesByName)
            .Where(process =>
            {
                try
                {
                    return !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
}

using System;
using System.Diagnostics;

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

    /// <summary>Kills every process with a name, for fixture teardown.</summary>
    /// <param name="processName">The name, without extension.</param>
    internal static void KillAll(string processName)
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
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

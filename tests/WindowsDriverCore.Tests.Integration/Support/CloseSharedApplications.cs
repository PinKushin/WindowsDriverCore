using NUnit.Framework;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Closes the shared applications once, after the whole run.
/// </summary>
/// <remarks>
/// <para>
/// A <c>SetUpFixture</c> with no namespace qualifier applies to the entire
/// assembly, so this is the only teardown that runs after every fixture rather
/// than after each one.
/// </para>
/// <para>
/// <b>The per-fixture teardowns it replaces were the problem.</b> Fourteen
/// fixtures each launched Calculator and most called
/// <c>AppLifetime.KillAll("CalculatorApp")</c> when they finished, so a run
/// booted it roughly ten times — most of the suite's runtime, and all of the
/// window-flashing. Worse, a <c>KillAll</c> from one fixture destroys the
/// instance another is still using, which is a failure that appears and
/// disappears with execution order.
/// </para>
/// <para>
/// Fixtures that deliberately destroy a window — the liveness, cache and
/// packaged-instance tests — still launch their own and kill it by process id.
/// Killing by name is what cannot coexist with a shared instance.
/// </para>
/// </remarks>
[SetUpFixture]
public sealed class CloseSharedApplications
{
    /// <summary>Closes the shared Calculator, and only that one.</summary>
    /// <remarks>
    /// <b>By process id, never by name.</b> KillAll("CalculatorApp") would close
    /// a Calculator the developer had open for their own reasons — a test suite
    /// reaching outside its own blast radius. Only the instance this suite
    /// started is its to close.
    ///
    /// Other applications are not touched here: the fixtures that launch
    /// Settings, charmap and the WPF subject own those and close them
    /// themselves.
    /// </remarks>
    [OneTimeTearDown]
    public void CloseEverything()
    {
        int processId = Support.SharedCalculator.ProcessId;
        if (processId != 0)
        {
            Support.AppLifetime.KillProcess(processId);
        }
    }
}

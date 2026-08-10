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
/// <b>Nothing here knows a process name any more, and that is the point.</b>
/// Fourteen fixtures each launched Calculator and most killed it by name when
/// they finished. That was wrong twice over: on Windows 11 it destroyed the
/// instance another fixture was still using, and on Windows 10 it matched
/// nothing at all, because the process there is called <c>Calculator</c> rather
/// than <c>CalculatorApp</c>. The shared application is now opened through the
/// driver and closed by ending its session.
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
        // Ends the SESSION, and the driver closes the application it started.
        // No process name, no process id, nothing for a test to get wrong on a
        // Windows version where the process is called something else.
        Support.SharedDriverSession.Close();
    }
}

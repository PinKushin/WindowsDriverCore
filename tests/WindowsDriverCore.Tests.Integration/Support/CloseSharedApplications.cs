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
    /// <b>By process id, never by name.</b> KillAll("Calculator") would close
    /// a Calculator the developer had open for their own reasons — a test suite
    /// reaching outside its own blast radius. Only the instance this suite
    /// started is its to close.
    ///
    /// Other applications are not touched here: the fixtures that launch
    /// Settings, charmap and the WPF subject own those and close them
    /// themselves.
    /// </remarks>
    /// <summary>Records what was already running, so leaks can be attributed.</summary>
    [OneTimeSetUp]
    public void RecordWhatWasAlreadyRunning() => Support.ProcessLeaks.Snapshot();

    /// <summary>Closes the shared applications once, after the whole run.</summary>
    /// <remarks>
    /// <b>The leak ASSERTION is not here, and that is deliberate.</b> A failure
    /// raised in a SetUpFixture teardown is reported in the output and then
    /// ignored: measured 2026-08-11, a deliberate leak printed its message and
    /// the run still finished "Passed! - Failed: 0" with exit code 0, whether it
    /// used Assert.Fail or threw. An instrument nothing counts is the very
    /// problem it was written to fix, so the assertion lives in a real test —
    /// see <c>ZzzzLeakedApplicationsTests</c>.
    /// </remarks>
    [OneTimeTearDown]
    public void CloseEverything() => Support.SharedDriverSession.Close();
}


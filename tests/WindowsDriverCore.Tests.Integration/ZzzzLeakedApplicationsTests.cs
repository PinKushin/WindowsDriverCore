using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Nothing this suite launched is still running.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real test, because a teardown cannot fail a run.</b> Measured
/// 2026-08-11: a deliberate leak asserted in the assembly teardown printed its
/// message and the run still reported "Passed! - Failed: 0" with exit code 0,
/// with both <c>Assert.Fail</c> and a thrown exception. A check nothing counts
/// is exactly the invisible failure it was written to catch.
/// </para>
/// <para>
/// <b>Named to sort last, because [Order] was not enough.</b> Measured: as
/// <c>ZzLeaked…</c> it ran BEFORE <c>ZzTempLeakOnPurpose</c> — alphabetical order
/// won — found nothing, and passed. The four z's are load-bearing.
///
/// It closes the shared session itself rather than waiting for the assembly
/// teardown, which runs after every fixture including this one.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
[Order(int.MaxValue)]
public sealed class ZzzzLeakedApplicationsTests
{
    [Test]
    public void NoApplicationThisSuiteLaunchedIsStillRunning()
    {
        // The shared application is this suite's to close, so close it before
        // asking what is left.
        SharedDriverSession.Close();

        IReadOnlyList<string> leaked = ProcessLeaks.TakeLeaks();

        leaked.ShouldBeEmpty(
            $"a fixture launched {string.Join(", ", leaked)} and did not close it. " +
            "They have been killed so the next run starts clean, but the leak is " +
            "the defect: whichever fixture opened it owns closing it.");
    }
}

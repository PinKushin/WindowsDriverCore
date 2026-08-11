using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The leak detector itself, tested directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Because the detector is a test, and a test that has never been red proves
/// nothing.</b> Two earlier versions of this check were green while a process
/// was demonstrably leaking: one asserted in a <c>SetUpFixture</c> teardown,
/// where a failure is printed and then ignored — measured, the run reported
/// "Passed! - Failed: 0" with exit code 0 for both <c>Assert.Fail</c> and a
/// thrown exception — and one ran before the fixture that leaked, because
/// alphabetical order beat <c>[Order]</c>.
/// </para>
/// <para>
/// This tests the mechanism rather than the wiring, so it does not depend on
/// fixture ordering at all.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class ProcessLeakDetectionTests
{
    [Test]
    public void AProcessStartedAfterTheSnapshot_IsReportedAndKilled()
    {
        ProcessLeaks.Snapshot();

        using Process leaked = Process.Start(
            new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;

        // Wait for it to be listed rather than assuming it is instant.
        UiSettle.Until(
            () => Process.GetProcessesByName("notepad").Any(p => p.Id == leaked.Id),
            TimeSpan.FromSeconds(10),
            "notepad to appear in the process list");

        IReadOnlyList<string> reported = ProcessLeaks.TakeLeaks();

        reported.ShouldContain(
            entry => entry.Contains($"({leaked.Id})", StringComparison.Ordinal),
            $"the detector must name the process it leaked; it reported: {string.Join(", ", reported)}");

        // And it must actually clean up, or every later run inherits the mess.
        UiSettle.Until(
            () => !Process.GetProcessesByName("notepad").Any(p => p.Id == leaked.Id),
            TimeSpan.FromSeconds(10),
            "the leaked process to be killed");
    }

    [Test]
    public void AProcessAlreadyRunningBeforeTheSnapshot_IsLeftAlone()
    {
        // THE CONTROL, and the one that matters most. A developer's own Calculator
        // is not the suite's to kill, and a detector that cannot tell the
        // difference would reach outside its blast radius.
        using Process theirs = Process.Start(
            new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;

        UiSettle.Until(
            () => Process.GetProcessesByName("notepad").Any(p => p.Id == theirs.Id),
            TimeSpan.FromSeconds(10),
            "notepad to appear in the process list");

        ProcessLeaks.Snapshot();

        try
        {
            ProcessLeaks.TakeLeaks().ShouldNotContain(
                entry => entry.Contains($"({theirs.Id})", StringComparison.Ordinal),
                "a process that predates the snapshot was not started by this suite");

            Process.GetProcessesByName("notepad").ShouldContain(
                p => p.Id == theirs.Id, "and it must still be running");
        }
        finally
        {
            theirs.Kill();
        }
    }
}

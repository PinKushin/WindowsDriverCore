using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Host.Diagnostics;

namespace WindowsDriverCore.Tests.Unit.Diagnostics;

/// <summary>
/// A local crash report, written before the process goes down.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a server run from a CLI, or from CI.</b> Nobody is necessarily
/// watching the console when it dies, so the report has to land somewhere
/// findable afterwards — a fixed, discoverable directory rather than a path
/// that has to be caught on the way past. The exact file written is also
/// printed to stdout at the moment of the crash, so a CI log has it archived
/// even when nobody was watching live.
/// </para>
/// <para>
/// <b>Local only, same rule as the request transcript.</b> Nothing here sends
/// anything off the machine — the report is a file, and what happens to it
/// after that is the operator's choice, not this driver's.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CrashDumpWriterTests
{
    private static readonly DateTimeOffset Fixed =
        new(2026, 8, 12, 3, 15, 40, 250, TimeSpan.Zero);

    private string _directory = null!;

    [SetUp]
    public void MakeATempDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wdc-crash-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void RemoveTheTempDirectory()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void TheDirectoryIsCreated_IfItDoesNotExist()
    {
        Directory.Exists(_directory).ShouldBeFalse("the test must start from a clean slate");

        CrashDumpWriter writer = new(_directory, new FixedClock(Fixed));
        writer.Write(new InvalidOperationException("boom"), isTerminating: true);

        Directory.Exists(_directory).ShouldBeTrue();
    }

    [Test]
    public void TheFileNameCarriesTheTimestamp_SoTwoCrashesDoNotOverwriteEachOther()
    {
        CrashDumpWriter writer = new(_directory, new FixedClock(Fixed));

        string path = writer.Write(new InvalidOperationException("boom"), isTerminating: true);

        Path.GetFileName(path).ShouldBe("crash-20260812-031540250.log");
    }

    [Test]
    public void TheReportNamesWhatHappened_AndWhetherTheProcessIsGoing()
    {
        CrashDumpWriter writer = new(_directory, new FixedClock(Fixed));

        // Actually THROWN, not merely constructed. A constructed-but-never-
        // thrown exception carries no stack trace at all, which would let this
        // test pass against a report with no location information in it — the
        // exact gap the assertion below exists to catch.
        Exception thrown;
        try
        {
            ThrowIt();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException caught)
        {
            thrown = caught;
        }

        string path = writer.Write(thrown, isTerminating: true);
        string content = File.ReadAllText(path);

        content.ShouldContain("InvalidOperationException");
        content.ShouldContain("the window was already gone");
        content.ShouldContain("Terminating: True");

        // The stack trace, not just the message — a report with only the
        // message tells a reader WHAT broke and nothing about where.
        content.ShouldContain("at ");
    }

    private static void ThrowIt() =>
        throw new InvalidOperationException("the window was already gone");

    [Test]
    public void ANonTerminatingReport_SaysSo()
    {
        // TaskScheduler.UnobservedTaskException fires for exceptions that do
        // NOT bring the process down, and a report that always claimed
        // "Terminating: True" would mislead whoever reads it about whether the
        // server is even still running.
        CrashDumpWriter writer = new(_directory, new FixedClock(Fixed));

        string path = writer.Write(new InvalidOperationException("boom"), isTerminating: false);

        File.ReadAllText(path).ShouldContain("Terminating: False");
    }

    [Test]
    public void WritingReturnsTheFullPath_ForTheCallerToPrint()
    {
        CrashDumpWriter writer = new(_directory, new FixedClock(Fixed));

        string path = writer.Write(new InvalidOperationException("boom"), isTerminating: true);

        path.ShouldBe(Path.Combine(_directory, "crash-20260812-031540250.log"));
    }

    [Test]
    public void ANullException_ThrowsRatherThanWritingAnEmptyReport()
    {
        CrashDumpWriter writer = new(_directory, new FixedClock(Fixed));

        Should.Throw<ArgumentNullException>(() => writer.Write(null!, isTerminating: true));
    }

    /// <summary>A clock that does not move, so the file name is asserted exactly.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

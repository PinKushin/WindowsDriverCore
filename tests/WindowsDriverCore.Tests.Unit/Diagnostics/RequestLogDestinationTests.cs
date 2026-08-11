using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Host.Diagnostics;

namespace WindowsDriverCore.Tests.Unit.Diagnostics;

/// <summary>
/// Where the transcript goes, and how that is chosen.
/// </summary>
/// <remarks>
/// <para>
/// <b>An environment variable rather than a command-line switch, for the same
/// reason the port has one.</b> The argument grammar is a compatibility contract
/// — a suite points at this driver with WinAppDriver's own arguments and must not
/// have to learn new ones. <c>ServerAddress</c> already rejects anything outside
/// the documented forms, and it should keep doing that.
/// </para>
/// <para>
/// Console is the default because that is what WinAppDriver does, and its
/// transcript is the only reason its failures are readable.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RequestLogDestinationTests
{
    private string _directory = null!;

    [SetUp]
    public void MakeATempDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wdc-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
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
    public void WithNoVariableSet_TheTranscriptGoesToTheConsole()
    {
        using RequestLogDestination destination = RequestLogDestination.Open(_ => null);

        destination.Writer.ShouldBeSameAs(
            Console.Out, "the default has to be the console, as WinAppDriver's is");

        Directory.GetFiles(_directory).ShouldBeEmpty(
            "nothing may be written to disk unless a path was asked for");
    }

    [Test]
    public void WithAPathSet_TheTranscriptGoesToThatFile()
    {
        string path = Path.Combine(_directory, "driver.log");

        using (RequestLogDestination destination = RequestLogDestination.Open(
            name => name == RequestLogDestination.PathEnvironmentVariable ? path : null))
        {
            destination.Writer.ShouldNotBeSameAs(Console.Out);
            destination.Writer.WriteLine("a line");
        }

        // Read after disposal, so this also establishes that the file is closed
        // rather than left locked by a still-running server.
        File.ReadAllText(path).ShouldBe("a line" + Environment.NewLine);
    }

    [Test]
    public void TheFileIsFlushedPerLine_NotAtExit()
    {
        // A driver that crashes loses exactly the request that killed it, which
        // is the one worth having. So the assertion is that the line is on disk
        // while the writer is still OPEN — buffering would leave the file empty
        // here and only look correct because disposal happened to run.
        string path = Path.Combine(_directory, "driver.log");

        using RequestLogDestination destination = RequestLogDestination.Open(
            name => name == RequestLogDestination.PathEnvironmentVariable ? path : null);

        destination.Writer.WriteLine("survives a crash");

        using FileStream reading = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(reading);

        reader.ReadToEnd().ShouldBe("survives a crash" + Environment.NewLine);
    }

    [Test]
    public void AnExistingFileIsAppendedTo_NotTruncated()
    {
        // Two runs against one path must not silently erase the first. A
        // transcript that vanishes when the server restarts is worse than none,
        // because the absence looks like the requests never happened.
        string path = Path.Combine(_directory, "driver.log");
        File.WriteAllText(path, "from an earlier run" + Environment.NewLine);

        using (RequestLogDestination destination = RequestLogDestination.Open(
            name => name == RequestLogDestination.PathEnvironmentVariable ? path : null))
        {
            destination.Writer.WriteLine("from this run");
        }

        File.ReadAllText(path).ShouldBe(
            "from an earlier run" + Environment.NewLine +
            "from this run" + Environment.NewLine);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Tests.Unit.Diagnostics;

/// <summary>
/// The buffer <c>POST /log</c> serves.
/// </summary>
/// <remarks>
/// <b>Fed by teeing the transcript rather than by reading the events again</b>,
/// which is what makes the privacy rule hold for free: locators are logged and
/// <c>SetValue</c>/<c>SendKeys</c> arguments never are, so serving the SAME
/// formatted lines means an HTTP log cannot contain a password the console
/// transcript would not.
/// </remarks>
[TestFixture]
public sealed class LogBufferTests : IDisposable
{
    private MovableClock _clock = null!;
    private LogBuffer _buffer = null!;

    [SetUp]
    public void Arrange()
    {
        _clock = new MovableClock();
        _buffer = new LogBuffer(_clock);
    }

    /// <summary>A written line becomes an entry.</summary>
    [Test]
    public void ALineWritten_BecomesAnEntry()
    {
        _buffer.WriteLine("POST /session -> 200 jwp 0 776.9 ms");

        IReadOnlyList<LogEntry> entries = _buffer.Drain();

        entries.Count.ShouldBe(1);
        entries[0].Message.ShouldBe("POST /session -> 200 jwp 0 776.9 ms");
        entries[0].Level.ShouldBe("INFO");
    }

    /// <summary>Draining empties it.</summary>
    /// <remarks>
    /// <b>The protocol's contract, not a convenience.</b> <c>POST /log</c>
    /// returns entries since the LAST call, and a client polls it — one reading a
    /// log that never drained would report every line again on every call.
    /// </remarks>
    [Test]
    public void Draining_TakesTheEntriesRatherThanCopyingThem()
    {
        _buffer.WriteLine("first");

        _buffer.Drain().Count.ShouldBe(1);
        _buffer.Drain().ShouldBeEmpty("the first drain took them");
    }

    /// <summary>A line written one character at a time still becomes one entry.</summary>
    /// <remarks>
    /// <c>TextWriter</c> guarantees only that every overload funnels through
    /// <c>Write(char)</c>, so a caller is entitled to write a line a character at
    /// a time. A buffer that split on writes rather than on newlines would turn
    /// one transcript line into forty entries.
    /// </remarks>
    [Test]
    public void ALineWrittenCharacterByCharacter_IsStillOneEntry()
    {
        foreach (char character in "find AutomationId='num5Button'\n")
        {
            _buffer.Write(character);
        }

        IReadOnlyList<LogEntry> entries = _buffer.Drain();

        entries.Count.ShouldBe(1);
        entries[0].Message.ShouldBe("find AutomationId='num5Button'");
    }

    /// <summary>Carriage returns do not survive into the entry.</summary>
    /// <remarks>
    /// <c>WriteLine</c> emits the platform newline, which on Windows is
    /// <c>\r\n</c>. A retained <c>\r</c> would appear inside the JSON string a
    /// client reads back.
    /// </remarks>
    [Test]
    public void AWindowsNewline_LeavesNoCarriageReturnInTheEntry()
    {
        _buffer.Write("click -> Performed via Invoke\r\n");

        _buffer.Drain()[0].Message.ShouldBe("click -> Performed via Invoke");
    }

    /// <summary>Blank lines are not entries.</summary>
    /// <remarks>
    /// The transcript separates sections with them. Forwarding them would pad a
    /// client's log with empty records it has to filter out.
    /// </remarks>
    [Test]
    public void BlankLines_AreNotEntries()
    {
        _buffer.WriteLine("real");
        _buffer.WriteLine(string.Empty);
        _buffer.WriteLine("also real");

        _buffer.Drain().Count.ShouldBe(2);
    }

    /// <summary>The buffer is bounded and drops the OLDEST.</summary>
    /// <remarks>
    /// <b>Both halves matter.</b> Unbounded is a leak with a slow fuse — a driver
    /// runs for days. Dropping the oldest rather than refusing the newest keeps
    /// the lines nearest a failure, which are the ones anybody asking for a log
    /// wants; a buffer that filled up and then ignored everything would answer
    /// with the first five thousand lines of the day.
    /// </remarks>
    [Test]
    public void PastCapacity_TheOldestLinesAreDropped()
    {
        for (int index = 0; index < LogBuffer.Capacity + 10; index++)
        {
            _buffer.WriteLine($"line {index}");
        }

        IReadOnlyList<LogEntry> entries = _buffer.Drain();

        entries.Count.ShouldBe(LogBuffer.Capacity);
        entries[0].Message.ShouldBe("line 10", "the first ten were dropped");
        entries[^1].Message.ShouldBe($"line {LogBuffer.Capacity + 9}", "the newest survived");
    }

    /// <summary>Each entry carries the time it was written.</summary>
    [Test]
    public void EachEntry_CarriesItsOwnTimestamp()
    {
        _buffer.WriteLine("first");
        _clock.Advance(TimeSpan.FromSeconds(5));
        _buffer.WriteLine("second");

        IReadOnlyList<LogEntry> entries = _buffer.Drain();

        (entries[1].Timestamp - entries[0].Timestamp).ShouldBe(5000);
    }

    /// <summary>The tee sends output to both writers.</summary>
    /// <remarks>
    /// THE CONTROL FOR THE WHOLE ARRANGEMENT. The transcript's existing
    /// destination — a console a person is watching, or a log file — must keep
    /// receiving everything it did before. A tee that swallowed the primary would
    /// silence the server's console and nothing else would notice.
    /// </remarks>
    [Test]
    public void TheTee_WritesToBothAndNeitherLosesOutput()
    {
        StringWriter console = new();
        LogBuffer buffer = new(_clock);

        using TeeTextWriter tee = new(console, buffer);

        tee.WriteLine("GET /status -> 200");

        console.ToString().ShouldContain("GET /status -> 200");
        buffer.Drain()[0].Message.ShouldBe("GET /status -> 200");
    }

    /// <summary>Disposes the buffer between tests.</summary>
    /// <remarks>
    /// A <c>TextWriter</c> is disposable, so the fixture that holds one is too —
    /// enforced by the analyzer rather than by taste, and correctly: a writer
    /// left undisposed in a long-lived fixture is exactly the leak this rule
    /// exists for.
    /// </remarks>
    public void Dispose() => _buffer?.Dispose();

    /// <summary>A clock the test moves, so a timestamp gap is asserted exactly.</summary>
    /// <remarks>
    /// Written here rather than taking a dependency on
    /// <c>Microsoft.Extensions.TimeProvider.Testing</c>: three lines, and the
    /// project already has the same shape in <c>CrashDumpWriterTests</c>.
    /// </remarks>
    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

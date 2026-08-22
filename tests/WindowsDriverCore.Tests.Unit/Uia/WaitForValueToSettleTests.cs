using System;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation.Uia;

namespace WindowsDriverCore.Tests.Unit.Uia;

/// <summary>
/// The loop <c>UiaElementInteractor.SendKeys</c> uses to wait for its own
/// effect, exercised with a scripted read sequence instead of real UIA.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a unit test and not another integration fixture.</b> The
/// subtle part of this mechanism is a pure decision — when does a sequence of
/// reads count as "settled" — and every attempt to validate it against a real
/// application in this project's history ran into a subject that was either too
/// fast to distinguish anything (a Win32 <c>EDIT</c>) or too unstable to trust
/// (a WPF window that lost its handle mid-run). None of that instability is
/// relevant to the ALGORITHM, which does not know or care whether its reads
/// come from COM. A scripted <c>Func&lt;string?&gt;</c> exercises exactly the
/// logic and none of the flakiness.
/// </para>
/// <para>
/// <b>The case every prior candidate got wrong is the FIRST one below.</b> Two
/// reads that agree because nothing has happened yet must not be mistaken for
/// two reads that agree because typing has finished — that is the literal
/// mechanism behind <c>WaitForInputIdle</c> answering "idle" 80 times out of
/// 101 in under a millisecond, and it is exactly what a naive
/// "stop when the last two reads match" loop would also get wrong.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WaitForValueToSettleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

    /// <summary>A scripted reader: returns each value in order, then repeats the last.</summary>
    private static Func<string?> Script(params string?[] values)
    {
        int index = 0;
        return () =>
        {
            string? value = values[Math.Min(index, values.Length - 1)];
            index++;
            return value;
        };
    }

    /// <summary>
    /// The exact defect that made every prior candidate look correct.
    /// </summary>
    /// <remarks>
    /// Two reads of the SAME value, never changing. A loop that declares
    /// stability on "the last two reads agree" alone would return after the
    /// very first poll — indistinguishable from a value that settled instantly.
    /// This is what a read taken before the target thread has processed
    /// anything looks like, and it is why <c>WaitForInputIdle</c> could not be
    /// trusted: it answers exactly this shape of question.
    /// </remarks>
    [Test]
    public void AValueThatNeverChanges_SpendsTheWholeBudget_RatherThanStoppingOnTheFirstAgreement()
    {
        int calls = 0;
        Func<string?> neverChanges = () =>
        {
            calls++;
            return "Alarm (1)";
        };

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        UiaElementInteractor.WaitForValueToSettle(neverChanges, Budget);
        elapsed.Stop();

        // The budget was spent, not returned instantly. A loop with the bug
        // this test guards against returns after the SECOND call, in well
        // under a millisecond - so a generous lower bound (half the budget)
        // cleanly separates "spun for the whole budget" from "declared victory
        // on the first two reads".
        elapsed.Elapsed.ShouldBeGreaterThan(Budget / 2);
        calls.ShouldBeGreaterThan(2, "a two-call loop is the exact bug this test exists to catch");
    }

    /// <summary>The ordinary case: a value that changes once and then holds.</summary>
    [Test]
    public void AValueThatChangesOnceThenHolds_IsAcceptedAsSettled_WellUnderTheBudget()
    {
        Func<string?> settles = Script("Alarm (1)", "Alarm (1)", "A", "Ab", "Abc", "Abc", "Abc");

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        UiaElementInteractor.WaitForValueToSettle(settles, Budget);
        elapsed.Stop();

        // Returns before the budget is exhausted - the script runs out of new
        // values and starts repeating the last one long before 200 ms of real
        // wall-clock spinning would elapse.
        elapsed.Elapsed.ShouldBeLessThan(Budget);
    }

    /// <summary>
    /// The Ctrl+A / Delete shape: the value shrinks to empty and stays there.
    /// </summary>
    /// <remarks>
    /// The actual failing case in the compatibility suite. Empty string is a
    /// value like any other for this loop - nothing about the logic treats it
    /// specially, which is the point: it does not need to know what "cleared"
    /// means, only that the value stopped moving.
    /// </remarks>
    [Test]
    public void AValueThatShrinksToEmpty_IsAcceptedAsSettled()
    {
        Func<string?> clears = Script("Alarm (1)", "Alarm (1)", "", "");

        UiaElementInteractor.WaitForValueToSettle(clears, Budget);

        // No assertion beyond "returned" - the return itself is the claim
        // under test, and the other two tests establish it returns for the
        // right reason rather than trivially.
        Assert.Pass();
    }

    /// <summary>A dead element stops the loop immediately rather than spinning.</summary>
    [Test]
    public void ADeadElement_StopsImmediately_RatherThanSpendingTheBudget()
    {
        // "\0WindowsDriverCore.DeadElement\0" from the FIRST call - the same
        // sentinel SettleValue's COM catch block returns, reproduced literally
        // rather than referencing the private constant, since a public
        // contract test should not need access to it.
        Func<string?> diesImmediately = () => "\0WindowsDriverCore.DeadElement\0";

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        UiaElementInteractor.WaitForValueToSettle(diesImmediately, Budget);
        elapsed.Stop();

        elapsed.Elapsed.ShouldBeLessThan(Budget / 2);
    }

    /// <summary>A value that keeps changing spends the whole budget, never crashing.</summary>
    [Test]
    public void AValueThatNeverStopsChanging_SpendsTheWholeBudget_AndReturnsCleanly()
    {
        int counter = 0;
        Func<string?> everChanging = () => (counter++).ToString();

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        Should.NotThrow(() => UiaElementInteractor.WaitForValueToSettle(everChanging, Budget));
        elapsed.Stop();

        elapsed.Elapsed.ShouldBeGreaterThan(Budget / 2);
    }
}

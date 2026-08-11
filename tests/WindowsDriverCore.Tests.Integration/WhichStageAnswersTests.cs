using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Which stage of the window search answers, and how long it takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because three separate claims about this waiter were credited to
/// the wrong mechanism.</b> That the CoreWindow is destroyed (it is reparented).
/// That an empty frame must be refused (refusing cost 20 tests). That rooting at
/// the frame won sixteen compatibility tests (the guest never got a frame-rooted
/// session). Each was a real effect attached to the wrong cause, and each time the
/// only observable was the handle — which cannot distinguish "the frame stage
/// answered" from "a CoreWindow was held to the deadline and returned".
/// </para>
/// <para>
/// The stage and the elapsed time distinguish them, so both are now returned by
/// <c>SearchAsync</c> and asserted here.
/// </para>
/// <para>
/// <b>The assertion is a budget, not a stage.</b> Which stage wins is legitimately
/// platform-dependent — the Windows 10 guest is a measuring instrument rather than
/// a support target, and its CoreWindow does not stop being top-level as promptly
/// as the host's. What is <i>not</i> acceptable anywhere is paying the full
/// ten-second timeout for every packaged session, which is what
/// <c>HeldCoreWindow</c> means.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class WhichStageAnswersTests
{
    /// <summary>Substring that matches Calculator on BOTH Windows versions.</summary>
    /// <remarks>
    /// Windows 10 names the process <c>Calculator</c> and Windows 11 names it
    /// <c>CalculatorApp</c>. <c>AppLifetime.KillAll</c> matches on substring, so
    /// "CalculatorApp" matches NOTHING on the guest — a cleanup that silently does
    /// nothing, which turns a cold-launch measurement into a re-attach without
    /// saying so. Measured 2026-08-11: a "cold" launch there reported 616 ms and an
    /// ApplicationFrameWindow because the application had never been closed.
    /// </remarks>
    private const string CalculatorProcess = "Calculator";

    private const string PackagedApplication =
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    /// <summary>
    /// What a packaged session may cost. The launcher's own timeout is 10 s, so
    /// anything at or near it means the search ran out rather than succeeded.
    /// </summary>
    private const long BudgetMs = 5000;

    [Test]
    public async Task APackagedLaunch_IsAnsweredByAStage_AndNotByTheDeadline()
    {
        AppLifetime.KillAll(CalculatorProcess);

        IReadOnlySet<nint> before = MainWindowWaiter.SnapshotTopLevelWindows();

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(PackagedApplication, null, null));

        if (launched.Application is null)
        {
            Assert.Fail($"The packaged application would not launch: {launched.FailureMessage}");
            return;
        }

        try
        {
            // Re-attach, which is the path the compatibility suite spends most of
            // its time in: the application is already running, so this measures
            // the search rather than the application's startup.
            MainWindowWaiter.WindowSearchResult found = await new MainWindowWaiter(TimeProvider.System)
                .SearchAsync(
                    launched.Application.ProcessId,
                    before,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromMilliseconds(100))
                .ConfigureAwait(false);

            await TestContext.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"source={found.Source} elapsed={found.ElapsedMs} ms window=0x{found.Window:X8}"))
                .ConfigureAwait(false);

            found.Window.ShouldNotBe(0, "the search must find the running application");

            found.Source.ShouldNotBe(
                MainWindowWaiter.WindowSource.HeldCoreWindow,
                "HeldCoreWindow means no frame ever arrived and the full timeout was " +
                "paid; every packaged session would cost ten seconds");

            found.ElapsedMs.ShouldBeLessThan(
                BudgetMs,
                "a search that takes this long has run out rather than succeeded");
        }
        finally
        {
            AppLifetime.KillAll(CalculatorProcess);
        }
    }

    [Test]
    public async Task AColdPackagedLaunch_IsAlsoAnsweredByAStage()
    {
        // **The path the re-attach test cannot see.** MEASURED on the guest:
        // re-attach answers HostedFrame in 4 ms, yet a cold launch there handed a
        // session a Windows.UI.Core.CoreWindow. Those cannot both be the same
        // search behaving the same way, so one of the inputs differs.
        //
        // The discriminator is built in: time the launch, then immediately run the
        // same search again using the HOSTED process id. If the second answers
        // HostedFrame at once while the launch did not, the difference is the
        // process id the waiter was given - activation returns one thing and the
        // window's content belongs to another - and not the frame's existence.
        AppLifetime.KillAll(CalculatorProcess);
        await Task.Delay(1500).ConfigureAwait(false);

        IReadOnlySet<nint> before = MainWindowWaiter.SnapshotTopLevelWindows();

        long began = System.Diagnostics.Stopwatch.GetTimestamp();

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(PackagedApplication, null, null));

        long launchMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;

        if (launched.Application is null)
        {
            Assert.Fail($"The packaged application would not launch: {launched.FailureMessage}");
            return;
        }

        try
        {
            string coldClass = ClassNameOf(launched.Application.WindowHandle);

            MainWindowWaiter.WindowSearchResult again = await new MainWindowWaiter(TimeProvider.System)
                .SearchAsync(
                    launched.Application.ProcessId,
                    before,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromMilliseconds(100))
                .ConfigureAwait(false);

            await TestContext.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"COLD launch: {launchMs} ms, window=0x{launched.Application.WindowHandle:X8} " +
                $"class={coldClass} hostedPid={launched.Application.ProcessId} | " +
                $"same search with hostedPid: source={again.Source} elapsed={again.ElapsedMs} ms " +
                $"window=0x{again.Window:X8}"))
                .ConfigureAwait(false);

            launchMs.ShouldBeLessThan(
                BudgetMs,
                $"a cold packaged launch cost {launchMs} ms and returned a '{coldClass}'; " +
                "at or near the 10 s timeout it ran out rather than succeeded");
        }
        finally
        {
            AppLifetime.KillAll(CalculatorProcess);
        }
    }

    private static string ClassNameOf(nint window)
    {
        char[] buffer = new char[256];
        int length = NativeMethods.GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport(
            "user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int GetClassName(
            nint window,
            [System.Runtime.InteropServices.Out] char[] className,
            int maxCount);
    }
}

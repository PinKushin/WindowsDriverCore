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
        AppLifetime.KillAll("CalculatorApp");

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
            AppLifetime.KillAll("CalculatorApp");
        }
    }
}

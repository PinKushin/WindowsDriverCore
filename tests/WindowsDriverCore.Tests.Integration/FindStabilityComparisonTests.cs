using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Interop.UIAutomationClient;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// The founding hypothesis, with a control.
/// </summary>
/// <remarks>
/// <para>
/// <b>H1:</b> querying the live UI Automation tree eliminates the class of
/// failure behind WinAppDriver #1079, where <c>FindElements</c> intermittently
/// returns nothing for an element that is present, because it searches a cached
/// view that has drifted from the screen.
/// </para>
/// <para>
/// <b>Manipulation:</b> the search implementation — this driver versus real
/// WinAppDriver. <b>Condition:</b> a stable element in a tree being mutated
/// underneath the query. <b>Measurement:</b> how many searches return zero
/// matches for an element that is definitely present. <b>Control:</b> the same
/// element, same application, same mutation, driven through WinAppDriver.
/// </para>
/// <para>
/// Without that control a green result here means only "the defect did not
/// occur", which is indistinguishable from "the defect is hard to trigger". That
/// is the whole reason this fixture exists rather than just repeating the search
/// against ourselves.
/// </para>
/// <para>
/// <b>RESULT, 2026-08-08 — H1 IS NOT SUPPORTED BY THIS CONDITION.</b> Ours
/// returned empty 0 times in 300; WinAppDriver also returned empty 0 times in
/// 300, with no failed requests. Both zero means the manipulation produced no
/// difference, so this condition is not sensitive to the defect. It is the
/// "wrong condition" case: an input for which a correct implementation and a
/// broken one predict the same observation.
/// </para>
/// <para>
/// The condition is the thing to fix, not the assertion. Clicking a digit
/// rewrites the display text but never destroys and recreates the searched
/// element's siblings. The field report behind #1079 (see
/// <c>docs/PROJECT-KNOWLEDGE.md</c>) reproduced it during a <c>CollectionView</c>
/// rebind — rows being removed and re-materialised. Calculator's memory list
/// does that, and is the next condition to try.
/// </para>
/// <para>
/// <b>Unplanned result, and the more useful one.</b> The same run measured 300
/// searches at roughly 33 ms each through this driver and roughly 1070 ms each
/// through WinAppDriver — about 32x. That bears on H3 rather than H1, and it was
/// not what the experiment was built to measure, so it is recorded as an
/// observation rather than presented as a benchmark. A proper comparison belongs
/// in the benchmark project, where the transports can be matched.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("Comparison")]
[NonParallelizable]
public sealed class FindStabilityComparisonTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    /// <summary>An element that exists for the whole run and never moves.</summary>
    private const string StableElement = "num5Button";

    /// <summary>Clicked repeatedly to keep the tree changing during the search.</summary>
    private const string MutatingElement = "num7Button";

    private const int Iterations = 300;
    private const int WinAppDriverPort = 4731;

    private static void KillCalculator()
    {
        foreach (Process process in Process.GetProcessesByName("CalculatorApp"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [Test]
    public void OurFinder_UnderTreeMutation_NeverReturnsEmptyForAPresentElement()
    {
        KillCalculator();

        WindowLocator windows = new();
        ApplicationLauncher launcher = new(new MainWindowWaiter(TimeProvider.System), windows);
        UiaElementFinder finder = new(new CUIAutomationClass());

        LaunchResult launched = launcher.Launch(new ApplicationTarget(CalculatorAumid, null, null));
        if (launched.Application is null)
        {
            Assert.Ignore($"Calculator is not available: {launched.FailureMessage}");
        }

        try
        {
            nint window = launched.Application.WindowHandle;

            // Guard the premise before measuring: if the element is not there to
            // begin with, "never returned empty" would be measuring nothing.
            finder.FindAll(window, LocatorKind.AutomationId, StableElement)
                .ElementIds.ShouldNotBeEmpty("the stable element must exist before the run");

            int emptyResults = 0;

            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                // Mutate: clicking a digit rewrites the display, which changes the
                // tree under the query without removing the element being searched
                // for.
                finder.FindAll(window, LocatorKind.AutomationId, MutatingElement);

                FindResult result = finder.FindAll(window, LocatorKind.AutomationId, StableElement);
                if (result.Failure != FindFailure.None || result.ElementIds.Count == 0)
                {
                    emptyResults++;
                }
            }

            TestContext.Out.WriteLine($"ours: {emptyResults}/{Iterations} searches returned nothing");

            emptyResults.ShouldBe(0);
        }
        finally
        {
            KillCalculator();
        }
    }

    [Test]
    public async Task WinAppDriver_UnderTheSameMutation_IsMeasuredForComparison()
    {
        // The control. It deliberately does NOT assert that WinAppDriver fails:
        // asserting a defect reproduces would make this suite red on any machine
        // or build where it happens not to, which is a probabilistic pass turned
        // into a probabilistic failure. It measures and reports.
        //
        // The comparison only supports H1 if this number is greater than zero on
        // the same run where ours is zero. If both are zero, the honest reading
        // is that this condition does not trigger the defect — not that the
        // defect is fixed.
        if (!WinAppDriverClient.IsInstalled)
        {
            Assert.Ignore("WinAppDriver is not installed; the control cannot run.");
        }

        KillCalculator();

        using WinAppDriverClient client = new(WinAppDriverPort);

        if (!await client.StartAsync(WinAppDriverPort))
        {
            Assert.Ignore("WinAppDriver did not start; the control cannot run.");
        }

        if (!await client.CreateSessionAsync(CalculatorAumid))
        {
            Assert.Ignore("WinAppDriver could not create a Calculator session.");
        }

        try
        {
            int before = await client.CountElementsAsync("accessibility id", StableElement);
            before.ShouldBeGreaterThan(
                0, "the stable element must exist before the run for this to measure anything");

            int emptyResults = 0;
            int failedRequests = 0;

            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                await client.ClickAsync(MutatingElement);

                int count = await client.CountElementsAsync("accessibility id", StableElement);

                if (count < 0)
                {
                    // The request itself failed. Counted separately so a transport
                    // problem cannot be mistaken for the defect being hunted.
                    failedRequests++;
                }
                else if (count == 0)
                {
                    emptyResults++;
                }
            }

            await TestContext.Out.WriteLineAsync(
                $"WinAppDriver: {emptyResults}/{Iterations} searches returned nothing, " +
                $"{failedRequests} requests failed outright").ConfigureAwait(false);

            // Recorded, not asserted. The number is the experimental result.
            Assert.Pass(
                $"Control measured: {emptyResults} empty of {Iterations} " +
                $"({failedRequests} request failures).");
        }
        finally
        {
            KillCalculator();
        }
    }
}

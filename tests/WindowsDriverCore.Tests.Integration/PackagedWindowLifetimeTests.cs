using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// What actually happens to a packaged application's windows after launch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because two readings of this on one machine disagreed.</b> One said
/// the application owned no top-level window 313 ms after launch — reported as
/// "the CoreWindow is destroyed". Another, minutes later, re-attached to the same
/// running application and got a live <c>Windows.UI.Core.CoreWindow</c>.
/// </para>
/// <para>
/// <b>The conflation was mine.</b> <c>EnumWindows</c> enumerates <i>top-level</i>
/// windows only. Once the CoreWindow is reparented into the frame it stops being
/// enumerated, so "no owned top-level window" means <b>reparented</b>, not
/// destroyed — and the two are the opposite design problem. Destruction means a
/// session's handle must be replaced; reparenting means it is still perfectly
/// valid and merely no longer findable the way it was found.
/// </para>
/// <para>
/// So this samples the timeline and distinguishes them by the only measurement
/// that can: whether the original handle is still <c>IsWindow</c>, and whether
/// <c>GetParent</c> has become non-zero. Handle identity is tracked, because
/// "a CoreWindow exists" and "the same CoreWindow exists" are different claims.
/// </para>
/// <para>
/// The assertion is the product requirement rather than either hypothesis: the
/// window a session is handed must still be a window once the frame has settled.
/// A driver whose session handle dies underneath it reports "currently selected
/// window has been closed" for everything afterwards.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class PackagedWindowLifetimeTests
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

    private const string FrameWindowClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private const string PackagedApplication =
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private const int SampleMs = 25;
    private const int WindowMs = 6000;

    [Test]
    public async Task TheWindowASessionIsHanded_IsStillAWindowOnceTheFrameHasSettled()
    {
        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(PackagedApplication, null, null));

        if (launched.Application is null)
        {
            Assert.Fail($"The packaged application would not launch: {launched.FailureMessage}");
            return;
        }

        nint handed = launched.Application.WindowHandle;
        int appProcess = launched.Application.ProcessId;

        try
        {
            List<string> timeline = [];
            long began = Stopwatch.GetTimestamp();

            nint firstCore = ClassNameOf(handed) == CoreWindowClass ? handed : 0;
            nint frameSeen = 0;
            bool reparentReported = false;
            bool deathReported = false;

            timeline.Add(Line(0, $"handed 0x{handed:X8} class={ClassNameOf(handed)} appPid={appProcess}"));

            while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < WindowMs)
            {
                long at = (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds;

                if (frameSeen == 0)
                {
                    nint frame = FrameHosting(appProcess);
                    if (frame != 0)
                    {
                        frameSeen = frame;
                        timeline.Add(Line(at, $"frame appeared 0x{frame:X8}"));
                    }
                }

                if (firstCore != 0)
                {
                    if (!IsWindow(firstCore) && !deathReported)
                    {
                        deathReported = true;
                        timeline.Add(Line(at, $"handed CoreWindow 0x{firstCore:X8} DESTROYED"));
                    }
                    else if (IsWindow(firstCore) && GetParent(firstCore) != 0 && !reparentReported)
                    {
                        reparentReported = true;
                        timeline.Add(Line(
                            at,
                            $"handed CoreWindow 0x{firstCore:X8} REPARENTED into 0x{GetParent(firstCore):X8} " +
                            $"(still IsWindow, no longer top-level)"));
                    }
                }

                // A fixed cadence is the INSTRUMENT here, not a wait on a
                // condition - the point is to sample what changes and when.
                await Task.Delay(SampleMs).ConfigureAwait(false);
            }

            long end = (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            timeline.Add(Line(
                end,
                $"final: handed IsWindow={IsWindow(handed)} parent=0x{GetParent(handed):X8} " +
                $"class={ClassNameOf(handed)} frame=0x{frameSeen:X8}"));

            await TestContext.Out.WriteLineAsync(string.Join(Environment.NewLine, timeline))
                .ConfigureAwait(false);

            IsWindow(handed).ShouldBeTrue(
                "the window a session is handed must outlive the frame settling, or every " +
                "later command reports that the selected window has been closed");
        }
        finally
        {
            AppLifetime.KillAll(CalculatorProcess);
        }
    }

    private static string Line(long milliseconds, string what) =>
        string.Create(CultureInfo.InvariantCulture, $"t={milliseconds,5} ms  {what}");

    private static nint FrameHosting(int processId)
    {
        nint match = 0;

        EnumWindows((frame, _) =>
        {
            if (!IsWindowVisible(frame) || ClassNameOf(frame) != FrameWindowClass)
            {
                return true;
            }

            EnumChildWindows(frame, (child, _) =>
            {
                if (ClassNameOf(child) != CoreWindowClass)
                {
                    return true;
                }

                uint thread = GetWindowThreadProcessId(child, out uint hosted);
                if (thread != 0 && (int)hosted == processId)
                {
                    match = frame;
                    return false;
                }

                return true;
            }, 0);

            return match == 0;
        }, 0);

        return match;
    }

    private static string ClassNameOf(nint window)
    {
        char[] buffer = new char[256];
        int length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private delegate bool EnumProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);
}

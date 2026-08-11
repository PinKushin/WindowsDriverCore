using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// An empty application frame is adopted, and that is deliberate.
/// </summary>
/// <remarks>
/// <para>
/// <b>This fixture pins a decision that looks like a bug.</b> An
/// <c>ApplicationFrameWindow</c> with no <c>Windows.UI.Core.CoreWindow</c> child
/// hosts nothing, and adopting one as a session's window hands every later find
/// an empty tree. Refusing it is the obvious fix. It was written, and it was
/// wrong.
/// </para>
/// <para>
/// <b>MEASURED 2026-08-11</b>, cold compatibility suite, Windows 10 22H2 guest,
/// alarm store reset, one run each:
/// </para>
/// <list type="bullet">
/// <item><description><c>e045b06</c>, no guard — <b>150</b>/290.</description></item>
/// <item><description><c>e6b6802</c>, empty frames refused — <b>130</b>/290, five
/// orphaned <c>CalculatorApp</c> processes left behind, and the entire
/// <c>*Error_NoSuchWindow</c> group failing.</description></item>
/// </list>
/// <para>
/// <b>As far as we can tell, the reason is timing.</b> A packaged application's
/// frame appears <i>before</i> its CoreWindow attaches, so for a second-or-later
/// session the empty frame is the right window and is merely unpopulated.
/// <c>FindFrameWindowHosting</c> cannot match it yet — it matches a frame through
/// a CoreWindow owned by the target process, and there is not one — so the loose
/// fallback taking it is what makes the attach work at all. Refusing it makes the
/// poll loop run to its deadline and answer "could not find main window", and the
/// orphaned processes are the sessions that then relaunched.
/// </para>
/// <para>
/// That is the <b>fourth</b> attempt on this seam to fail the same way, after
/// preferring the frame, rejecting the CoreWindow, and resolving through
/// <c>GA_ROOT</c>. A ranking variant — adopt the empty frame, but never over a
/// real window — was written and NOT kept: three different conditions all passed
/// with the ranking mutated away, so it could not be shown to do anything. An
/// unfalsifiable improvement is not an improvement.
/// </para>
/// <para>
/// The real fix waits for the frame to be populated rather than choosing between
/// windows at one instant, and it needs a UIA readiness check this layer cannot
/// make — <c>Platform</c> is Win32-only by design. Until then this exists so the
/// fifth attempt costs one test run instead of a compatibility suite.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class EmptyFrameAdoptionTests
{
    private const string FrameWindowClass = "ApplicationFrameWindow";

    /// <summary>
    /// A process id no process has, so every precise stage of the search misses
    /// and the loose fallback is the one under test.
    /// </summary>
    private const int NoSuchProcess = 0x7FFFFFFE;

    [SetUp]
    public void TheChosenProcessIdMustReallyBeAbsent()
    {
        // Or an ownership stage answers first and this fixture measures nothing.
        Should.Throw<ArgumentException>(() => Process.GetProcessById(NoSuchProcess));
    }

    [Test]
    public async Task AnEmptyApplicationFrame_IsAdopted_BecauseRefusingItCostTwentyTests()
    {
        // The subject is synthesised because the condition cannot be produced on
        // demand: killing a hosted application destroys its own frame — measured
        // 2026-08-11 — and the window that is genuinely empty is empty only during
        // a race nobody can time. The search branches on two observable facts, the
        // class name and the presence of a CoreWindow child, and a window
        // registered under that class name with no children supplies both.
        IReadOnlySet<nint> before = MainWindowWaiter.SnapshotTopLevelWindows();

        using RawWindow frame = RawWindow.Create(FrameWindowClass, "Calculator");

        nint found = await new MainWindowWaiter(TimeProvider.System).WaitAsync(
            NoSuchProcess, before, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50));

        found.ShouldBe(
            frame.Handle,
            "refusing an empty frame scored 130 against 150 on the compatibility suite");
    }

    [Test]
    public async Task AnOrdinaryNewWindow_IsAdopted()
    {
        // The control. Without it the test above is satisfied by a fallback that
        // adopts anything at all, and the fallback's actual job — taking a window
        // that appeared after the launch — would go untested.
        IReadOnlySet<nint> before = MainWindowWaiter.SnapshotTopLevelWindows();

        using RawWindow ordinary = RawWindow.Create(
            "WindowsDriverCoreTestSubjectWindow", "Subject");

        nint found = await new MainWindowWaiter(TimeProvider.System).WaitAsync(
            NoSuchProcess, before, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50));

        found.ShouldBe(ordinary.Handle);
    }
}

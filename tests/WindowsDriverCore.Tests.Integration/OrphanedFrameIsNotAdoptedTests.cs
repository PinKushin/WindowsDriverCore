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
/// The loose window fallback must not adopt an empty application frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured 2026-08-11.</b> An <c>ApplicationFrameWindow</c> outlives the
/// application it hosted: one was found alive on this desktop — <c>0x000502A8</c>,
/// visible, three children, <b>no</b> <c>Windows.UI.Core.CoreWindow</c> — and the
/// Windows 10 guest carried one from 04:10 through every compatibility run of the
/// day. Two runs of the same commit that hour scored 147 and 153; the difference
/// was the leftover, not the code.
/// </para>
/// <para>
/// A frame with no CoreWindow child hosts nothing. Adopting it as a session's
/// window gives every subsequent find an empty tree, and the session reports no
/// such element rather than reporting that it attached to the wrong window.
/// </para>
/// <para>
/// The precise stage of the search — <c>FindFrameWindowHosting</c> — cannot make
/// this mistake, because it matches a frame <i>through</i> a CoreWindow child
/// owned by the target process. Only the loose fallback can, which takes any
/// visible unowned window that was not there a moment ago, and a frame created
/// before its CoreWindow attaches is exactly that.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class OrphanedFrameIsNotAdoptedTests
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
        // Or the ownership stage answers first and this fixture measures nothing.
        Should.Throw<ArgumentException>(() => Process.GetProcessById(NoSuchProcess));
    }

    [Test]
    public async Task AnEmptyApplicationFrame_IsNotAdopted()
    {
        IReadOnlySet<nint> before = MainWindowWaiter.SnapshotTopLevelWindows();

        // Titled, deliberately. PreferTitled would push an untitled window behind
        // a titled one, and then the title rather than the guard would be doing
        // the work — the test would pass with the guard removed.
        using RawWindow frame = RawWindow.Create(FrameWindowClass, "Calculator");

        nint found = await new MainWindowWaiter(TimeProvider.System).WaitAsync(
            NoSuchProcess, before, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50));

        found.ShouldNotBe(frame.Handle, "an application frame hosting nothing is not a main window");
        found.ShouldBe(0, "and there was nothing else for the search to find");
    }

    [Test]
    public async Task AnOrdinaryNewWindow_IsStillAdopted()
    {
        // The control. Without it, a guard that rejected every new window would
        // pass the test above, and the loose fallback exists precisely to adopt
        // windows like this one.
        IReadOnlySet<nint> before = MainWindowWaiter.SnapshotTopLevelWindows();

        using RawWindow ordinary = RawWindow.Create(
            "WindowsDriverCoreTestSubjectWindow", "Subject");

        nint found = await new MainWindowWaiter(TimeProvider.System).WaitAsync(
            NoSuchProcess, before, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50));

        found.ShouldBe(ordinary.Handle);
    }
}

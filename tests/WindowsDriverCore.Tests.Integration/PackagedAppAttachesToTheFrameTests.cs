using System;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Tests.Integration.Support;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A packaged application's session attaches to its frame, not to the CoreWindow
/// that is about to be destroyed.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED 2026-08-11</b>, Calculator on Windows 11, polled every 250 ms from
/// launch:
/// </para>
/// <code>
/// t= 59 ms  owned=0x013B050C (Windows.UI.Core.CoreWindow)  frameHosting=0
/// t=313 ms  owned=0                                        frameHosting=0x018F07CE
/// </code>
/// <para>
/// The ownership search answers in about 60 ms with a <c>Windows.UI.Core.CoreWindow</c>,
/// and that window is <b>gone</b> by the time the hosting <c>ApplicationFrameWindow</c>
/// exists, roughly 300 ms later. A session handed the CoreWindow therefore holds a
/// handle that dies on its own, which is what surfaces much later as "Currently
/// selected window has been closed".
/// </para>
/// <para>
/// <b>The class name is the whole measurement, and it needs no clock.</b> A
/// CoreWindow is the defect and a frame is the fix, so the two hypotheses predict
/// different observations from one property read. An earlier version of this
/// fixture synthesised windows to test the same thing and asserted on elapsed
/// time; it was deleted. Its subject was a visible top-level window with no
/// message pump, which blocks every <c>SendMessage(HWND_BROADCAST)</c> on the
/// desktop until it times out — it wedged the real Calculator into a blank window
/// and hung the test host. A hazard to the machine running the tests is not an
/// acceptable instrument when the real application answers the same question.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class PackagedAppAttachesToTheFrameTests
{
    private const string FrameWindowClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private const string PackagedApplication =
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [Test]
    public void APackagedApplication_AttachesToItsFrame_NotToTheDoomedCoreWindow()
    {
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
            nint window = launched.Application.WindowHandle;
            string className = ClassNameOf(window);

            className.ShouldNotBe(
                CoreWindowClass,
                "the CoreWindow is destroyed about 300 ms after launch, so a session " +
                "holding it reports 'currently selected window has been closed' later");

            className.ShouldBe(FrameWindowClass);

            // The window must still be there once the CoreWindow's lifetime has
            // demonstrably elapsed. Synchronised on the frame being findable
            // rather than on a delay: the launcher already waited for exactly
            // that, so this reads it back rather than sleeping on a guess.
            new WindowLocator().Exists(window)
                .ShouldBeTrue("and the window a session is given must outlive the launch");
        }
        finally
        {
            new ApplicationTerminator().Terminate(launched.Application.ProcessId);
            AppLifetime.KillAll("CalculatorApp");
        }
    }

    [Test]
    public void AClassicApplication_AttachesToItsOwnWindow_AndIsNotDelayed()
    {
        // The control. The frame handling must not change what a plain Win32
        // application gets, and a classic window is neither a frame nor a
        // CoreWindow — so a waiter that had started answering "frame or nothing"
        // would fail here.
        string? found = TestApp.Path;
        if (found is null)
        {
            Assert.Ignore("The WPF test subject has not been built.");
            return;
        }

        string path = found;

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(path, null, null));

        try
        {
            launched.Application.ShouldNotBeNull();

            string className = ClassNameOf(launched.Application.WindowHandle);
            className.ShouldNotBe(FrameWindowClass);
            className.ShouldNotBe(CoreWindowClass);
        }
        finally
        {
            AppLifetime.KillAll(TestApp.ProcessName);
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

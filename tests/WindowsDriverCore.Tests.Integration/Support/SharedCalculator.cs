using System;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// One Calculator, shared by the fixtures that only read from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fourteen fixtures were launching their own.</b> A full integration run
/// booted Calculator about ten times, each with its own settle, which is most of
/// the suite's runtime and all of the window-flashing.
/// </para>
/// <para>
/// <b>Only for fixtures that read.</b> Anything that clicks changes the display,
/// and a fixture asserting on it would then depend on execution order — which is
/// the kind of shared-state coupling that makes a suite fail in one order and
/// pass in another. Those keep their own instance.
/// </para>
/// <para>
/// <b>Liveness is re-checked on every access, not cached.</b> Several fixtures
/// call <c>AppLifetime.KillAll("CalculatorApp")</c> in teardown, which destroys
/// this one too. Handing back a dead handle would produce <c>NoSuchWindow</c> in
/// whichever fixture happened to run next — a failure that moves when tests are
/// reordered and reads as flake. Re-launching instead makes order irrelevant.
/// </para>
/// </remarks>
internal static class SharedCalculator
{
    public const string Aumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private static readonly object Gate = new();
    private static nint _window;
    private static int _processId;

    /// <summary>The process this launched, or zero.</summary>
    /// <remarks>
    /// Recorded so teardown can kill THIS Calculator rather than every one on
    /// the machine. Killing by name would close a Calculator the developer had
    /// open for their own reasons, which is a test suite reaching outside its
    /// own blast radius.
    /// </remarks>
    public static int ProcessId
    {
        get { lock (Gate) { return _processId; } }
    }

    /// <summary>The shared window, launched or relaunched as needed.</summary>
    /// <returns>The window handle, or zero if Calculator is unavailable.</returns>
    public static nint Window()
    {
        lock (Gate)
        {
            if (_window != 0 && AppLifetime.WindowExists(_window))
            {
                return _window;
            }

            ApplicationLauncher launcher = new(
                new MainWindowWaiter(TimeProvider.System), new WindowLocator());

            LaunchResult launched = launcher.Launch(new ApplicationTarget(Aumid, null, null));
            _window = launched.Application?.WindowHandle ?? 0;
            _processId = launched.Application?.ProcessId ?? 0;

            return _window;
        }
    }
}

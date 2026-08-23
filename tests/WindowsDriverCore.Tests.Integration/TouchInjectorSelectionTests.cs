using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// Which injector touch actually goes through, and that the other one still works.
/// </summary>
/// <remarks>
/// <para>
/// <b>This test is the only thing protecting the Windows 10 1607 floor.</b> The
/// platform-compatibility analyzer does not help: CA1416 was forced to error
/// against a deliberately unguarded <c>InputInjector</c> call and the build
/// still succeeded, because the WinRT projections carry no
/// <c>SupportedOSPlatform</c> attributes. Nothing in the build fails if the
/// fallback is broken or removed.
/// </para>
/// <para>
/// So the fallback is asserted here instead — and asserted as BEHAVIOUR rather
/// than as a flag, because a flag says which path was chosen and not whether the
/// other one still functions.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
public sealed class TouchInjectorSelectionTests
{
    /// <summary>Says which path this machine took, so a run is self-describing.</summary>
    /// <remarks>
    /// Not an assertion: both answers are correct, and which one appears depends
    /// entirely on the Windows build underneath. Recorded because every other
    /// result in this fixture is read differently depending on it.
    /// </remarks>
    [Test]
    public void ReportWhichInjectorThisMachineUses()
    {
        SyntheticPointer pointer = new();

        TestContext.Out.WriteLine(
            pointer.UsesWinRtForTouch
                ? "WinRT InputInjector (Windows 10 1809 or newer)"
                : "Win32 InjectTouchInput fallback (below 1809, or injector unavailable)");

        Assert.Pass("Diagnostic: records the injection path for this machine.");
    }

    /// <summary>The Win32 path is still there and still works.</summary>
    /// <remarks>
    /// <b>The regression this guards is silent.</b> If the WinRT path becomes the
    /// only working one, a 1607 machine gets a driver that reports success and
    /// injects nothing — and no build, on any machine that has WinRT, would say
    /// so. Touch injection is initialised process-wide, so asking the Win32 layer
    /// directly is the closest a machine with WinRT can get to being a machine
    /// without it.
    /// </remarks>
    [Test]
    [Category("SynthesisesRealInput")]
    public void TheWin32FallbackStillInitialises()
    {
        SyntheticPointer pointer = new();

        // CanInject goes through the Win32 initialisation regardless of which
        // path Inject would later choose, so this fails if the fallback has been
        // broken even on a machine that never uses it.
        pointer.CanInject(SyntheticPointerKind.Touch).ShouldBeTrue(
            "the Win32 touch path must remain usable - it is the only path a " +
            "Windows 10 1607 machine has");
    }
}

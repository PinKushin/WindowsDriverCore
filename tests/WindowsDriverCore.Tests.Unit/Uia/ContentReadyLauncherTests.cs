using System;
using Interop.UIAutomationClient;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;

namespace WindowsDriverCore.Tests.Unit.Uia;

/// <summary>
/// A launch waits for the application to have something addressable.
/// </summary>
/// <remarks>
/// Measured on the guest: a launch answered while the frame was still empty and
/// a find 26 ms later missed, six times in nineteen. The wait closes that race —
/// and must never turn into a refusal, because refusing an empty frame was
/// measured at 130 against 150.
/// </remarks>
[TestFixture]
public sealed class ContentReadyLauncherTests
{
    private const nint TheWindow = 0x1234;

    private static ApplicationTarget AnyTarget => new("Calculator", null, null);

    private static LaunchResult Succeeded =>
        LaunchResult.Success(new LaunchedApplication(42, TheWindow));

    [Test]
    public void WhenContentIsAlreadyThere_TheLaunchIsNotDelayed()
    {
        IApplicationLauncher inner = Substitute.For<IApplicationLauncher>();
        inner.Launch(Arg.Any<ApplicationTarget>()).Returns(Succeeded);

        IUIAutomation automation = WithContent(present: true);

        ContentReadyLauncher launcher = new(inner, automation, TimeProvider.System);

        LaunchResult result = launcher.Launch(AnyTarget);

        result.Application!.WindowHandle.ShouldBe(TheWindow);

        // Asked once and satisfied: the wait costs nothing when the application
        // is ready, which is the common case and the one the speed goal cares
        // about.
        automation.Received(1).ElementFromHandle(TheWindow);
    }

    [Test]
    public void WhenContentNeverArrives_TheLaunchStillSucceeds()
    {
        // THE ONE THAT MATTERS. Refusing a frame with no content was measured at
        // 130 against 150 - twenty tests. This waits and then proceeds anyway,
        // so a slow or contentless application still gets a session.
        IApplicationLauncher inner = Substitute.For<IApplicationLauncher>();
        inner.Launch(Arg.Any<ApplicationTarget>()).Returns(Succeeded);

        IUIAutomation automation = WithContent(present: false);

        ContentReadyLauncher launcher = new(inner, automation, TimeProvider.System);

        LaunchResult result = launcher.Launch(AnyTarget);

        result.Application.ShouldNotBeNull("the launch must not be refused for lack of content");
        result.Application.WindowHandle.ShouldBe(TheWindow);
        result.FailureMessage.ShouldBeNull();
    }

    [Test]
    public void AFailedLaunch_IsPassedBackUntouched_AndNothingIsWaitedFor()
    {
        // There is no window to wait on, and the launcher's own message is what
        // the client matches against - so it must travel back unchanged.
        IApplicationLauncher inner = Substitute.For<IApplicationLauncher>();
        inner.Launch(Arg.Any<ApplicationTarget>())
            .Returns(new LaunchResult(null, "The system cannot find the file specified"));

        IUIAutomation automation = Substitute.For<IUIAutomation>();

        ContentReadyLauncher launcher = new(inner, automation, TimeProvider.System);

        LaunchResult result = launcher.Launch(AnyTarget);

        result.Application.ShouldBeNull();
        result.FailureMessage.ShouldBe("The system cannot find the file specified");
        automation.DidNotReceive().ElementFromHandle(Arg.Any<nint>());
    }

    [Test]
    public void AWindowThatVanishesWhileWaiting_EndsTheWait_RatherThanSpinning()
    {
        // The window can close between the launch and the check. That is the
        // caller's problem to discover on its next command, not a reason to burn
        // the whole budget here.
        IApplicationLauncher inner = Substitute.For<IApplicationLauncher>();
        inner.Launch(Arg.Any<ApplicationTarget>()).Returns(Succeeded);

        IUIAutomation automation = Substitute.For<IUIAutomation>();
        automation.ElementFromHandle(Arg.Any<nint>()).Returns((IUIAutomationElement?)null);

        ContentReadyLauncher launcher = new(inner, automation, TimeProvider.System);

        LaunchResult result = launcher.Launch(AnyTarget);

        result.Application!.WindowHandle.ShouldBe(TheWindow);
        automation.Received(1).ElementFromHandle(TheWindow);
    }

    /// <summary>An automation whose frame either exposes an addressable element or does not.</summary>
    private static IUIAutomation WithContent(bool present)
    {
        IUIAutomation automation = Substitute.For<IUIAutomation>();
        IUIAutomationElement root = Substitute.For<IUIAutomationElement>();
        IUIAutomationCondition condition = Substitute.For<IUIAutomationCondition>();

        automation.ElementFromHandle(Arg.Any<nint>()).Returns(root);
        automation.CreatePropertyCondition(Arg.Any<int>(), Arg.Any<object>()).Returns(condition);
        automation.CreateNotCondition(Arg.Any<IUIAutomationCondition>()).Returns(condition);

        root.FindFirst(Arg.Any<TreeScope>(), Arg.Any<IUIAutomationCondition>())
            .Returns(present ? Substitute.For<IUIAutomationElement>() : null);

        return automation;
    }
}

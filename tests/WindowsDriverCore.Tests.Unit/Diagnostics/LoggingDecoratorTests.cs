using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Diagnostics;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Diagnostics;

namespace WindowsDriverCore.Tests.Unit.Diagnostics;

/// <summary>
/// The decorators that put the automation layers into the transcript.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decorators rather than a constructor parameter, and that is a measured
/// choice.</b> <c>UiaElementFinder</c> and <c>UiaElementInteractor</c> are
/// constructed at nineteen call sites across fifteen files, almost all of them
/// UI tests that drive a real desktop. Threading a log through every one of them
/// would have been a large mechanical change to code that cannot be re-run
/// cheaply, to add a concern none of those tests care about.
/// </para>
/// <para>
/// So the logging lives outside: the real implementations are untouched, the
/// decorators are wired only in the composition root, and this fixture tests
/// them against fakes in milliseconds rather than against a live application.
/// </para>
/// <para>
/// <b>Every test here asserts the inner result is returned unchanged as well as
/// that the event was recorded.</b> A decorator that logs correctly and drops or
/// alters the answer would be a far worse defect than no logging at all, and
/// "the event was written" alone cannot see it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LoggingDecoratorTests
{
    [Test]
    public void AFind_IsRecordedWithItsLocatorAndMatchCount()
    {
        RecordingLog log = new();
        LoggingElementFinder finder = new(new StubFinder(FindResult.Matched(["1.2", "3.4"])), log);

        FindResult result = finder.FindAll(
            new SearchScope(42, null), LocatorKind.AutomationId, "num5Button");

        result.ElementIds.ShouldBe(["1.2", "3.4"], "the inner result must pass through unaltered");

        log.Finds.Count.ShouldBe(1);
        log.Finds[0].LocatorKind.ShouldBe("AutomationId");
        log.Finds[0].LocatorValue.ShouldBe("num5Button");
        log.Finds[0].Matches.ShouldBe(2);
        log.Finds[0].Failure.ShouldBeEmpty("the search ran");
    }

    [Test]
    public void AFindThatCouldNotRun_IsDistinguishedFromOneThatMatchedNothing()
    {
        // The condition where correct and broken differ. Both produce zero
        // element ids, and they are opposite diagnoses: no matches is a fact
        // about the application, a failure is a fact about the driver. A
        // transcript that rendered them the same would send every investigation
        // to the wrong place.
        RecordingLog log = new();

        new LoggingElementFinder(new StubFinder(FindResult.Matched([])), log)
            .FindAll(new SearchScope(42, null), LocatorKind.Name, "absent");

        new LoggingElementFinder(new StubFinder(FindResult.Failed(FindFailure.NoSuchWindow)), log)
            .FindAll(new SearchScope(42, null), LocatorKind.Name, "absent");

        log.Finds.Count.ShouldBe(2);

        log.Finds[0].Matches.ShouldBe(0);
        log.Finds[0].Failure.ShouldBeEmpty("it ran and matched nothing");

        log.Finds[1].Matches.ShouldBe(0);
        log.Finds[1].Failure.ShouldBe("NoSuchWindow", "it could not run");
    }

    [Test]
    public void AClick_IsRecordedWithTheRungThatActed()
    {
        // The rung is the whole point. A pattern invoke, an ancestor climb and a
        // real mouse click all report status 0 on the wire, and when the climb
        // toggled an app bar instead of pressing the button nothing downstream
        // could tell - see docs/CLICK-SEMANTICS.md.
        RecordingLog log = new();
        LoggingElementInteractor interactor = new(
            new StubInteractor(ElementAction.Performed("ancestor:1/Toggle")), log);

        ElementAction action = interactor.Click(42, "1.2");

        action.Path.ShouldBe("ancestor:1/Toggle", "the inner result must pass through unaltered");

        log.Actions.Count.ShouldBe(1);
        log.Actions[0].Action.ShouldBe("Click");
        log.Actions[0].Outcome.ShouldBe("Performed");
        log.Actions[0].Path.ShouldBe("ancestor:1/Toggle");
    }

    [Test]
    public void SetValue_RecordsTheActionAndNeverTheValue()
    {
        // THE PRIVACY GUARANTEE, asserted rather than asserted-about. This route
        // is how a suite types a password into an application, so the value must
        // not appear anywhere in what the decorator hands the log.
        const string Secret = "hunter2-correct-horse-battery-staple";

        RecordingLog log = new();
        LoggingElementInteractor interactor = new(
            new StubInteractor(ElementAction.Performed("Value")), log);

        interactor.SetValue(42, "1.2", Secret);

        log.Actions.Count.ShouldBe(1);
        log.Actions[0].Action.ShouldBe("SetValue", "the command itself is worth knowing");

        foreach (string field in new[]
        {
            log.Actions[0].Action, log.Actions[0].Outcome, log.Actions[0].Path,
        })
        {
            field.ShouldNotContain(Secret);
        }
    }

    [Test]
    public void SendKeys_RecordsTheActionAndNeverTheKeystrokes()
    {
        const string Secret = "p@ssw0rd";

        RecordingLog log = new();
        LoggingElementInteractor interactor = new(
            new StubInteractor(ElementAction.Failed(ElementActionOutcome.NotInteractable)), log);

        interactor.SendKeys(42, "1.2", Secret);

        log.Actions[0].Action.ShouldBe("SendKeys");
        log.Actions[0].Outcome.ShouldBe("NotInteractable");

        foreach (string field in new[]
        {
            log.Actions[0].Action, log.Actions[0].Outcome, log.Actions[0].Path,
        })
        {
            field.ShouldNotContain(Secret);
        }
    }

    [Test]
    public void AFailedLaunch_IsRecordedWithItsReason()
    {
        RecordingLog log = new();
        LoggingApplicationLauncher launcher = new(
            new StubLauncher(LaunchResult.Failure("The system cannot find the file specified.")),
            log);

        LaunchResult result = launcher.Launch(new ApplicationTarget("nope.exe", null, null));

        result.Application.ShouldBeNull("the inner result must pass through unaltered");

        log.Launches.Count.ShouldBe(1);
        log.Launches[0].App.ShouldBe("nope.exe");
        log.Launches[0].ProcessId.ShouldBe(0);
        log.Launches[0].Window.ShouldBe(0);
        log.Launches[0].Failure.ShouldBe("The system cannot find the file specified.");
    }

    [Test]
    public void ASuccessfulLaunch_IsRecordedWithItsProcessAndWindow()
    {
        RecordingLog log = new();
        LoggingApplicationLauncher launcher = new(
            new StubLauncher(LaunchResult.Success(new LaunchedApplication(1234, 0x00A1B2C3))),
            log);

        launcher.Launch(new ApplicationTarget("Calculator", null, null));

        log.Launches[0].ProcessId.ShouldBe(1234);
        log.Launches[0].Window.ShouldBe(0x00A1B2C3);
        log.Launches[0].Failure.ShouldBeEmpty();
    }

    [Test]
    public void ATerminationThatLeftTheProcessRunning_IsRecordedAsSuch()
    {
        // The false is the interesting case, and it is silent everywhere else. A
        // session that ends while its application keeps running is how the next
        // run inherits a warm application it did not ask for and measures a
        // re-attach as a cold launch - which has already been misread as a code
        // change twice in this project.
        RecordingLog log = new();
        LoggingApplicationTerminator terminator = new(new StubTerminator(ended: false), log);

        terminator.Terminate(4321, 0).ShouldBeFalse("the inner result must pass through unaltered");

        log.Terminations.Count.ShouldBe(1);
        log.Terminations[0].ProcessId.ShouldBe(4321);
        log.Terminations[0].Ended.ShouldBeFalse();
    }

    private sealed record LoggedFind(
        string LocatorKind, string LocatorValue, int Matches, string Failure, double Elapsed);

    private sealed record LoggedAction(
        string Action, string Outcome, string Path, double Elapsed);

    private sealed record LoggedLaunch(
        string App, int ProcessId, long Window, string WindowClass, string Failure, double Elapsed);

    private sealed record LoggedTermination(int ProcessId, bool Ended, double Elapsed);

    private sealed class RecordingLog : IFindLog, IInteractionLog, ILaunchLog, ITerminationLog
    {
        internal List<LoggedFind> Finds { get; } = [];

        internal List<LoggedAction> Actions { get; } = [];

        internal List<LoggedLaunch> Launches { get; } = [];

        internal List<LoggedTermination> Terminations { get; } = [];

        public void FindCompleted(
            string locatorKind,
            string locatorValue,
            int matches,
            string failure,
            double elapsedMilliseconds) =>
            Finds.Add(new LoggedFind(
                locatorKind, locatorValue, matches, failure, elapsedMilliseconds));

        public void ElementActionCompleted(
            string action, string outcome, string path, double elapsedMilliseconds) =>
            Actions.Add(new LoggedAction(action, outcome, path, elapsedMilliseconds));

        public void ApplicationLaunched(
            string app,
            int processId,
            long window,
            string windowClass,
            string failure,
            double elapsedMilliseconds) =>
            Launches.Add(new LoggedLaunch(
                app, processId, window, windowClass, failure, elapsedMilliseconds));

        public void ApplicationTerminated(
            int processId, bool ended, double elapsedMilliseconds) =>
            Terminations.Add(new LoggedTermination(processId, ended, elapsedMilliseconds));
    }

    private sealed class StubFinder(FindResult result) : IElementFinder
    {
        public FindResult FindAll(SearchScope scope, LocatorKind kind, string value) => result;

        public FindResult FindFirst(SearchScope scope, LocatorKind kind, string value) => result;
    }

    private sealed class StubInteractor(ElementAction action) : IElementInteractor
    {
        public ElementAction Click(nint window, string elementId) => action;

        public ElementAction Clear(nint window, string elementId) => action;

        public ElementAction SetValue(nint window, string elementId, string value) => action;

        public ElementAction SendKeys(nint window, string elementId, string keys) => action;

        public ElementAction TypeValue(nint window, string elementId, string keys) => action;
    }

    private sealed class StubLauncher(LaunchResult result) : IApplicationLauncher
    {
        public LaunchResult Launch(ApplicationTarget target) => result;
    }

    private sealed class StubTerminator(bool ended) : IApplicationTerminator
    {
        public bool Terminate(int processId, nint window) => ended;
    }
}

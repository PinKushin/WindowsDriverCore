namespace WindowsDriverCore.Diagnostics;

/// <summary>
/// Records what a search was asked for and what it returned.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap a request-only transcript leaves.</b> A slow or empty
/// <c>POST /element</c> says a find failed and nothing about which locator,
/// against what, or how long the tree walk took — which is exactly the question
/// every "element could not be located" failure in the compatibility suite
/// raises.
/// </para>
/// <para>
/// <b>Where the payload line is drawn, stated rather than implied.</b> A locator
/// IS recorded, including its value. That is not a retreat from
/// <see cref="IRequestLog"/>'s rule: a locator is a query the test author wrote,
/// not data the driver typed into an application. What stays unlogged is
/// everything this driver <i>transmits into</i> the application under test —
/// <c>SetValue</c> and <c>SendKeys</c> payloads — because that is where a
/// password appears. <see cref="IInteractionLog"/> holds that line by having no
/// parameter for it.
/// </para>
/// </remarks>
public interface IFindLog
{
    /// <summary>Records a finished search.</summary>
    /// <param name="locatorKind">The locator strategy, e.g. <c>AutomationId</c>.</param>
    /// <param name="locatorValue">What was searched for.</param>
    /// <param name="matches">How many elements matched.</param>
    /// <param name="failure">
    /// Why the search could not run, or empty when it ran. Distinct from zero
    /// matches: a search that ran and matched nothing is a fact about the
    /// application, and a search that could not run is a fact about the driver.
    /// </param>
    /// <param name="elapsedMilliseconds">Wall-clock cost of the search.</param>
    void FindCompleted(
        string locatorKind,
        string locatorValue,
        int matches,
        string failure,
        double elapsedMilliseconds);
}

/// <summary>
/// Records the lookup that turns an element id back into a live element.
/// </summary>
/// <remarks>
/// <b>The driver's main performance claim is invisible without it.</b> Resolving
/// an id walks the tree at a measured 19.4 ms; the same lookup against a held
/// handle is 0.45 ms. Which of those happened is not visible in any request line
/// — the whole command just takes longer — so a cache that quietly stopped
/// hitting would look like a slow application.
/// </remarks>
public interface IResolveLog
{
    /// <summary>Records a finished lookup.</summary>
    /// <param name="outcome">Resolved, NotFound or NoSuchWindow.</param>
    /// <param name="elapsedMilliseconds">
    /// Wall-clock cost, which is what distinguishes a cached handle from a full
    /// tree walk.
    /// </param>
    void ElementResolved(string outcome, double elapsedMilliseconds);
}

/// <summary>
/// Records a page-source read and how big the answer was.
/// </summary>
/// <remarks>
/// <b>The size is why this exists.</b> A <c>GET /source</c> request is almost
/// entirely the read, so the request line already carries the cost — but not the
/// document. 37,413 characters against 19,656 is the difference between a dialog
/// being open and not, and that distinction is what half a day of the 2026-08-11
/// investigation turned on.
/// </remarks>
public interface IPageSourceLog
{
    /// <summary>Records a finished read.</summary>
    /// <param name="characters">
    /// Document length, or <c>-1</c> when the window was gone. Distinguished
    /// because an empty document is a real answer for an empty window.
    /// </param>
    /// <param name="elapsedMilliseconds">Wall-clock cost.</param>
    void PageSourceRead(int characters, double elapsedMilliseconds);
}

/// <summary>
/// Records which rung of the click ladder acted, and what came of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the rung was invisible and that cost twelve tests.</b> A
/// click that reaches its target through a pattern, through an ancestor, and
/// through real mouse input are three different events which the wire reports
/// identically as status 0. When the ancestor climb toggled an app bar instead
/// of pressing "Add new alarm", nothing downstream could tell — see
/// <c>docs/CLICK-SEMANTICS.md</c>.
/// </para>
/// <para>
/// <b>No parameter carries a payload.</b> The action's NAME is recorded and its
/// value is not, so <c>SetValue</c> appears in the transcript and the string it
/// set never does. That is the same structural guarantee as
/// <see cref="IRequestLog"/>: not a redactor that must stay correct, but a shape
/// with nothing to redact.
/// </para>
/// </remarks>
public interface IInteractionLog
{
    /// <summary>Records a finished element action.</summary>
    /// <param name="action">The command, e.g. <c>Click</c> or <c>SetValue</c>.</param>
    /// <param name="outcome">The outcome, e.g. <c>Performed</c>.</param>
    /// <param name="path">
    /// Which rung acted — <c>Invoke</c>, <c>ancestor:1/Toggle</c>, <c>mouse</c>.
    /// Empty when nothing acted.
    /// </param>
    /// <param name="elapsedMilliseconds">Wall-clock cost.</param>
    void ElementActionCompleted(
        string action,
        string outcome,
        string path,
        double elapsedMilliseconds);
}

/// <summary>
/// Records where a pointer command aimed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two 200s and no effect is the case this exists for.</b> The compatibility
/// suite deletes its alarms with <c>Mouse.ContextClick</c> — a <c>/moveto</c>
/// then a <c>/click</c> with button 2. Measured 2026-08-11: both answered 200,
/// no context menu appeared, and the <c>find Name='Delete'</c> after them
/// matched nothing. The transcript could say the input was dispatched and
/// nothing about where it went, so there was no way to tell a wrong coordinate
/// from a coordinate the system delivered elsewhere.
/// </para>
/// <para>
/// <b>The rectangle rides along because the point alone is ambiguous.</b> UIA
/// answers with an empty rectangle for an element it can see but cannot place,
/// and the centre of nothing is <c>(0,0)</c> — the top-left of the screen, which
/// reads as an ordinary coordinate. Beside a size of <c>0x0</c> it does not.
/// </para>
/// <para>
/// <b>This does not weaken <see cref="IInteractionLog"/>'s rule.</b> A
/// coordinate is where this driver aimed, which is the driver's own behaviour.
/// No parameter here can carry a string the caller supplied, so there is still
/// nothing to redact.
/// </para>
/// </remarks>
public interface IPointerLog
{
    /// <summary>Records a dispatched pointer command.</summary>
    /// <param name="command">
    /// What was asked for — <c>moveto</c>, <c>click button 2</c>. A command name,
    /// never an argument.
    /// </param>
    /// <param name="x">Screen x aimed at.</param>
    /// <param name="y">Screen y aimed at.</param>
    /// <param name="width">
    /// Width of the element the point came from, <c>-1</c> when the command had
    /// no element. Absent and empty must stay distinguishable: <c>0</c> means UIA
    /// could not place an element it could see, and <c>-1</c> means there was
    /// nothing to place.
    /// </param>
    /// <param name="height">Height of that element, on the same rule.</param>
    /// <param name="elapsedMilliseconds">Wall-clock cost.</param>
    void PointerTargeted(
        string command,
        int x,
        int y,
        int width,
        int height,
        double elapsedMilliseconds);
}

/// <summary>
/// Records how an application was reached and how long it took.
/// </summary>
/// <remarks>
/// <b>Three separate claims about the window search were credited to the wrong
/// mechanism</b> because the only observable was the handle — see
/// <c>WhichStageAnswersTests</c>. That fixture exists to answer at test time what
/// this answers at run time, which is the difference between reproducing a
/// launch problem and reading what happened on the machine where it occurred.
/// </remarks>
public interface ILaunchLog
{
    /// <summary>Records a finished launch attempt.</summary>
    /// <param name="app">What was asked for — a path or an application id.</param>
    /// <param name="processId">The tracked process, or <c>0</c> on failure.</param>
    /// <param name="window">The window handle, or <c>0</c> on failure.</param>
    /// <param name="windowClass">
    /// The window's class. <c>ApplicationFrameWindow</c> against
    /// <c>Windows.UI.Core.CoreWindow</c> is the discriminator behind three claims
    /// about the window search that were each credited to the wrong mechanism,
    /// and a handle on its own cannot show it.
    /// </param>
    /// <param name="failure">Why it failed, or empty on success.</param>
    /// <param name="elapsedMilliseconds">
    /// Wall-clock cost. A launch at or near the ten-second timeout ran out rather
    /// than succeeded, and that is invisible in a result that carries only a
    /// handle.
    /// </param>
    void ApplicationLaunched(
        string app,
        int processId,
        long window,
        string windowClass,
        string failure,
        double elapsedMilliseconds);
}

/// <summary>
/// Records the end of an application this driver started.
/// </summary>
/// <remarks>
/// <b>A safety record, not a convenience.</b> <c>DELETE /session</c> terminates
/// the tracked process, and for a packaged application the window's OWNER is
/// <c>ApplicationFrameHost</c> — a broker shared by every UWP window on the
/// machine. Aimed there, one session teardown closes all of them. The pid is
/// therefore worth having written down at the moment it is used, not only
/// reconstructible afterwards.
/// </remarks>
public interface ITerminationLog
{
    /// <summary>Records an attempt to end a process.</summary>
    /// <param name="processId">The process aimed at.</param>
    /// <param name="ended">Whether it is no longer running.</param>
    /// <param name="elapsedMilliseconds">Wall-clock cost.</param>
    void ApplicationTerminated(int processId, bool ended, double elapsedMilliseconds);

    /// <summary>Records closing a window and whether it actually went.</summary>
    /// <param name="windowHandle">The window asked to close.</param>
    /// <param name="gone">
    /// Whether it was observed to disappear within the bounded wait. FALSE is
    /// the interesting case: the close still answers success because the request
    /// was delivered, so a window that outlives the wait is invisible on the
    /// wire - and a command issued straight afterwards sees it alive and reports
    /// "no such element" where the caller expects "window has been closed".
    /// </param>
    /// <param name="elapsedMilliseconds">Wall-clock cost of the close and the wait.</param>
    void WindowClosed(nint windowHandle, bool gone, double elapsedMilliseconds);

    /// <summary>Records a keystroke dispatch and whether the window was raised first.</summary>
    /// <param name="raised">
    /// Whether the window actually came to the foreground. Synthesized keys go
    /// to whatever holds focus, NOT to a handle, so a false here means the
    /// keystrokes went somewhere else - and the request still answered success,
    /// because refusing to type deadlocks a caller trying to dismiss a shell
    /// surface it just opened.
    /// </param>
    /// <param name="elapsedMilliseconds">Wall-clock cost of the dispatch.</param>
    /// <remarks>
    /// <b>No key content, ever.</b> This is where a password would appear, so the
    /// event carries only whether the raise worked. That constraint is the reason
    /// the parameter list cannot grow to include what was typed.
    /// </remarks>
    /// <param name="foreground">
    /// What held the foreground when the raise FAILED, or empty when it
    /// succeeded. A window title and process, not a handle - the consumer is a
    /// person reading a transcript, and "0x1A03C6" answers nothing an hour
    /// later.
    /// </param>
    void KeysDispatched(bool raised, string foreground, double elapsedMilliseconds);

    /// <summary>Records the wait for typed input to be consumed.</summary>
    /// <param name="waited">
    /// Whether the wait actually ran. FALSE is the interesting case and it is
    /// currently invisible: <c>WaitForInputProcessed</c> returns false when the
    /// window is gone, when the process id is zero, or when <c>OpenProcess</c> is
    /// denied — and the caller carries on, because a missing wait is a race
    /// while refusing the read is a certainty. So a drain that never ran and a
    /// drain that waited produce the same transcript.
    /// </param>
    /// <param name="elapsedMilliseconds">
    /// Wall-clock cost. Worth reading beside <paramref name="waited"/>: a TRUE
    /// that cost 0 ms means the wait ran and answered immediately, which is a
    /// different problem from the wait not running at all.
    /// </param>
    /// <remarks>
    /// <b>Added 2026-08-12 to answer a question three theories failed to.</b> The
    /// <c>SendKeysToElement_*</c> family reads an element's text 0.9 ms after the
    /// keystroke that should have changed it, and nothing in the transcript said
    /// whether the drain in between did anything. The same argument as
    /// <see cref="WindowClosed"/>: a boolean the code already computes and throws
    /// away is exactly the boolean an investigation later needs.
    /// </remarks>
    void InputDrained(bool waited, double elapsedMilliseconds);
}

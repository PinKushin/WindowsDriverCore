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
}

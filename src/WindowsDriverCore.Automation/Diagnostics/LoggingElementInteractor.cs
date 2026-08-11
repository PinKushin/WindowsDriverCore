using System.Diagnostics;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Automation.Diagnostics;

/// <summary>
/// Puts every element action into the transcript, including which rung acted.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rung is the reason this exists.</b> A click that lands through a
/// pattern, through an ancestor climb and through real mouse input are three
/// different events, and all three report status 0 on the wire. When the climb
/// reached <c>AlarmCollectionPageCommandBar</c> and toggled an app bar instead
/// of pressing "Add new alarm", the client was told the button was clicked and
/// nothing downstream could tell otherwise — see <c>docs/CLICK-SEMANTICS.md</c>.
/// <c>ElementAction.Path</c> already carries the rung; this makes it observable
/// at run time instead of only in a test.
/// </para>
/// <para>
/// <b>Values are never passed to the log.</b> <c>SetValue</c> and
/// <c>SendKeys</c> carry whatever a suite types into the application, which is
/// where a password appears. The command's name is recorded and its argument is
/// not — and <see cref="IInteractionLog"/> has no parameter that could take it,
/// so this is a property of the shape rather than of this method staying
/// careful.
/// </para>
/// </remarks>
public sealed class LoggingElementInteractor : IElementInteractor
{
    private readonly IElementInteractor _inner;
    private readonly IInteractionLog _log;

    /// <summary>Wraps an interactor.</summary>
    /// <param name="inner">The interactor that does the work.</param>
    /// <param name="log">Where actions are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LoggingElementInteractor(IElementInteractor inner, IInteractionLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public ElementAction Click(nint window, string elementId) =>
        Recorded(() => _inner.Click(window, elementId), nameof(Click));

    /// <inheritdoc />
    public ElementAction Clear(nint window, string elementId) =>
        Recorded(() => _inner.Clear(window, elementId), nameof(Clear));

    /// <inheritdoc />
    public ElementAction SetValue(nint window, string elementId, string value) =>
        Recorded(() => _inner.SetValue(window, elementId, value), nameof(SetValue));

    /// <inheritdoc />
    public ElementAction SendKeys(nint window, string elementId, string keys) =>
        Recorded(() => _inner.SendKeys(window, elementId, keys), nameof(SendKeys));

    private ElementAction Recorded(Func<ElementAction> act, string action)
    {
        long began = Stopwatch.GetTimestamp();
        ElementAction result;

        try
        {
            result = act();
        }
        catch (Exception exception)
        {
            // Recorded and rethrown: an action that threw is the line worth
            // having, and swallowing it would change behaviour to tidy a log.
            _log.ElementActionCompleted(
                action,
                exception.GetType().Name,
                string.Empty,
                Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            throw;
        }

        _log.ElementActionCompleted(
            action,
            result.Outcome.ToString(),
            result.Path ?? string.Empty,
            Stopwatch.GetElapsedTime(began).TotalMilliseconds);

        return result;
    }
}

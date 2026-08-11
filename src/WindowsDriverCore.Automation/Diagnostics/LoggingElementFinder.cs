using System.Diagnostics;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Automation.Diagnostics;

/// <summary>
/// Puts every search into the transcript, without the finder knowing.
/// </summary>
/// <remarks>
/// <para>
/// <b>A decorator, not a constructor parameter.</b> <c>UiaElementFinder</c> is
/// built at nineteen call sites across fifteen files, nearly all of them UI tests
/// that drive a real desktop. Threading a log through all of them would be a
/// large mechanical change to code that is expensive to re-run, in service of a
/// concern none of those tests have. Here the finder is untouched and the
/// decorator is wired only in the composition root.
/// </para>
/// <para>
/// <b>It measures the find alone.</b> The request transcript already times the
/// whole call; the difference between the two numbers is this driver's own
/// overhead, which is the thing the benchmark budget is about.
/// </para>
/// </remarks>
public sealed class LoggingElementFinder : IElementFinder
{
    private readonly IElementFinder _inner;
    private readonly IFindLog _log;

    /// <summary>Wraps a finder.</summary>
    /// <param name="inner">The finder that does the work.</param>
    /// <param name="log">Where searches are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LoggingElementFinder(IElementFinder inner, IFindLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public FindResult FindAll(SearchScope scope, LocatorKind kind, string value) =>
        Recorded(() => _inner.FindAll(scope, kind, value), kind, value);

    /// <inheritdoc />
    public FindResult FindFirst(SearchScope scope, LocatorKind kind, string value) =>
        Recorded(() => _inner.FindFirst(scope, kind, value), kind, value);

    private FindResult Recorded(Func<FindResult> search, LocatorKind kind, string value)
    {
        long began = Stopwatch.GetTimestamp();
        FindResult result;

        try
        {
            result = search();
        }
        catch (Exception exception)
        {
            // Recorded and rethrown. A find that threw is the most interesting
            // line in any transcript, and swallowing it here would change the
            // driver's behaviour to make the logging tidier.
            _log.FindCompleted(
                kind.ToString(),
                value,
                matches: 0,
                failure: exception.GetType().Name,
                Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            throw;
        }

        // Zero matches and a failure are DIFFERENT and are recorded separately.
        // Both produce an empty id list, and they are opposite diagnoses: no
        // matches is a fact about the application, a failure is a fact about the
        // driver.
        _log.FindCompleted(
            kind.ToString(),
            value,
            result.ElementIds.Count,
            result.Failure == FindFailure.None ? string.Empty : result.Failure.ToString(),
            Stopwatch.GetElapsedTime(began).TotalMilliseconds);

        return result;
    }
}

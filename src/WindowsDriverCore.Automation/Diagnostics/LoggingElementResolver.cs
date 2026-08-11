using System.Diagnostics;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Automation.Diagnostics;

/// <summary>
/// Times the lookup that turns an element id back into a live element.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cost is the measurement.</b> Resolving an id by walking the tree was
/// measured at 19.4 ms against 0.45 ms for the same lookup through a held
/// handle, which is why <c>CachingElementResolver</c> exists. Nothing in a
/// request line separates those: a cache that quietly stopped hitting would look
/// like a slow application, and the driver's central performance claim would
/// degrade with no symptom.
/// </para>
/// <para>
/// <b>The result is passed straight back, undisposed.</b>
/// <see cref="ElementLookupResult"/> owns a COM element and the caller owns the
/// result — a decorator that wrapped it in a <c>using</c> to be tidy would
/// release the element before its consumer touched it.
/// </para>
/// <para>
/// It is deliberately indented one level deeper than an action in the
/// transcript, because a resolve happens inside one.
/// </para>
/// <para>
/// The element id is not recorded, and that is not a privacy decision: an id is
/// a UIA RuntimeId this driver issued moments earlier, and the enclosing request
/// line already carries it in the route. A column that is always redundant is
/// noise.
/// </para>
/// </remarks>
public sealed class LoggingElementResolver : IElementResolver
{
    private readonly IElementResolver _inner;
    private readonly IResolveLog _log;

    /// <summary>Wraps a resolver.</summary>
    /// <param name="inner">The resolver that does the work.</param>
    /// <param name="log">Where lookups are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LoggingElementResolver(IElementResolver inner, IResolveLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public ElementLookupResult Resolve(nint searchRoot, string elementId)
    {
        long began = Stopwatch.GetTimestamp();

        try
        {
            ElementLookupResult result = _inner.Resolve(searchRoot, elementId);

            _log.ElementResolved(
                result.Outcome.ToString(), Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            return result;
        }
        catch (Exception exception)
        {
            _log.ElementResolved(
                exception.GetType().Name, Stopwatch.GetElapsedTime(began).TotalMilliseconds);

            throw;
        }
    }
}

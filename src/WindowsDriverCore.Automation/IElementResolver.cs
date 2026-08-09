using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation;

/// <summary>What happened when an element id was looked up.</summary>
public enum ElementLookupOutcome
{
    /// <summary>The id names a live element.</summary>
    Resolved,

    /// <summary>
    /// No element in the tree has that id. Whether that is a stale element or an
    /// id this server never issued is deliberately not decided here — see
    /// <c>IElementRegistry</c>.
    /// </summary>
    NotFound,

    /// <summary>The search root window no longer exists.</summary>
    NoSuchWindow,
}

/// <summary>
/// A resolved element, owned by the caller.
/// </summary>
/// <remarks>
/// Disposable because a COM object is reference counted and the runtime's
/// finalizer-based release is non-deterministic. The implementation being
/// replaced stored elements in a dictionary and released none of them, leaking a
/// runtime callable wrapper per lookup.
/// </remarks>
public sealed class ElementLookupResult : IDisposable
{
    private IUIAutomationElement? _element;

    private ElementLookupResult(IUIAutomationElement? element, ElementLookupOutcome outcome)
    {
        _element = element;
        Outcome = outcome;
    }

    /// <summary>What happened.</summary>
    public ElementLookupOutcome Outcome { get; }

    /// <summary>The element, when one was found.</summary>
    public IUIAutomationElement? Element => _element;

    /// <summary>An id that named a live element.</summary>
    /// <param name="element">The element, whose lifetime passes to this result.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public static ElementLookupResult Resolved(IUIAutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return new ElementLookupResult(element, ElementLookupOutcome.Resolved);
    }

    /// <summary>A lookup that found nothing.</summary>
    /// <param name="outcome">Why, which must not be <see cref="ElementLookupOutcome.Resolved"/>.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> claims success without an element.
    /// </exception>
    public static ElementLookupResult Failed(ElementLookupOutcome outcome)
    {
        if (outcome == ElementLookupOutcome.Resolved)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome, "A resolved lookup must carry an element.");
        }

        return new ElementLookupResult(null, outcome);
    }

    /// <summary>Releases the element.</summary>
    public void Dispose()
    {
        if (_element is not null)
        {
            Marshal.ReleaseComObject(_element);
            _element = null;
        }
    }
}

/// <summary>
/// Turns an element id back into a live element.
/// </summary>
/// <remarks>
/// <para>
/// Every element command carries an id and nothing else, so this step sits in
/// front of all of them. It is also where a stale element is detected: the id
/// simply stops resolving.
/// </para>
/// <para>
/// Resolution re-queries the live tree rather than reading a cache, for the same
/// reason the finder does. That costs a descendant walk per element command,
/// which is the trade being made knowingly — a cache of elements is precisely
/// the design that produces the defects this driver exists to avoid.
/// </para>
/// </remarks>
public interface IElementResolver
{
    /// <summary>Looks up an element by its id.</summary>
    /// <param name="searchRoot">The window to search within.</param>
    /// <param name="elementId">The id, as this driver issued it.</param>
    /// <returns>The element, or why it could not be produced.</returns>
    ElementLookupResult Resolve(nint searchRoot, string elementId);
}

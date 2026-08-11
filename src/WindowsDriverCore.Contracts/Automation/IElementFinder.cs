using WindowsDriverCore.Automation.Locators;

namespace WindowsDriverCore.Automation;

/// <summary>Why a find could not be performed.</summary>
public enum FindFailure
{
    /// <summary>The find ran.</summary>
    None,

    /// <summary>The search root window no longer exists.</summary>
    NoSuchWindow,

    /// <summary>An XPath expression could not be evaluated.</summary>
    XPathLookupError,
}

/// <summary>Where a search runs.</summary>
/// <param name="Window">The session's window.</param>
/// <param name="ContainerElementId">
/// An element to search inside, or <see langword="null"/> to search the whole
/// window.
/// </param>
/// <remarks>
/// <para>
/// Scoping a search to a container is the protocol's answer to duplicate and
/// unnamed elements: rows that are indistinguishable across a window are usually
/// unique within their own list. Measured against WinAppDriver — searching for
/// <c>CalculatorResults</c> inside the keypad answers <c>no such element</c>
/// while the same locator at window scope finds it.
/// </para>
/// <para>
/// A record struct with an implicit conversion from <c>nint</c>, so the common
/// case still reads <c>FindAll(window, kind, value)</c> and only nested finds
/// mention scope at all.
/// </para>
/// </remarks>
public readonly record struct SearchScope(nint Window, string? ContainerElementId = null)
{
    /// <summary>A search over a whole window.</summary>
    /// <param name="window">The window.</param>
    public static implicit operator SearchScope(nint window) => new(window);

    /// <summary>A search over a whole window.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The scope.</returns>
    public static SearchScope FromWindow(nint window) => new(window);
}

/// <summary>The outcome of a find.</summary>
/// <param name="ElementIds">
/// Matching element ids, in tree order. Empty when nothing matched, which is
/// not a failure: <c>POST /elements</c> answers 200 with an empty array.
/// </param>
/// <param name="Failure">Why the find could not run, if it could not.</param>
public sealed record FindResult(IReadOnlyList<string> ElementIds, FindFailure Failure)
{
    /// <summary>A find that ran.</summary>
    /// <param name="elementIds">What it matched, possibly nothing.</param>
    /// <returns>The result.</returns>
    public static FindResult Matched(IReadOnlyList<string> elementIds) =>
        new(elementIds, FindFailure.None);

    /// <summary>A find that could not run.</summary>
    /// <param name="failure">Why.</param>
    /// <returns>The result.</returns>
    public static FindResult Failed(FindFailure failure) => new([], failure);
}

/// <summary>
/// Finds elements in a window's UI Automation tree.
/// </summary>
/// <remarks>
/// A find is a query, so it runs against the tree as it is now rather than
/// against a retained result set. That is not a claim about WinAppDriver's
/// defects — an earlier version of this comment attributed #857 and #1079 to a
/// cached view, and <c>docs/FOUNDING-PREMISE.md</c> retracts both descriptions.
/// It is simply what a search means: a result set kept between calls would
/// answer about a UI that has moved on.
///
/// Note what this does <b>not</b> forbid. Holding a live
/// <c>IUIAutomationElement</c> for an element the caller already has an id for
/// is a different thing, measured in <c>HeldElementLivenessTests</c>: such a
/// reference is a proxy, not a snapshot.
/// </remarks>
public interface IElementFinder
{
    /// <summary>Finds every element matching a locator.</summary>
    /// <param name="scope">The window, and optionally an element to search inside.</param>
    /// <param name="kind">What to match on.</param>
    /// <param name="value">The value to match.</param>
    /// <returns>The matching element ids, or why the search could not run.</returns>
    FindResult FindAll(SearchScope scope, LocatorKind kind, string value);

    /// <summary>Finds the first element matching a locator.</summary>
    /// <param name="scope">The window, and optionally an element to search inside.</param>
    /// <param name="kind">What to match on.</param>
    /// <param name="value">The value to match.</param>
    /// <returns>At most one element id, or why the search could not run.</returns>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="FindAll"/> because UIA can stop at the first
    /// match, and <c>POST /element</c> never uses more than one. Measured on
    /// Calculator: an exhaustive walk is 12.0 ms and a first-match walk is
    /// 9.8 ms, which is roughly three quarters of the gap to FlaUI.
    /// </para>
    /// <para>
    /// It must agree with <c>FindAll(...).ElementIds[0]</c>. UIA returns matches
    /// in tree order for both, so "first" means the same thing — but that is a
    /// claim about UIA rather than about this code, so a test asserts it against
    /// a real application.
    /// </para>
    /// </remarks>
    FindResult FindFirst(SearchScope scope, LocatorKind kind, string value);
}

using System.Collections.Generic;
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
/// The reason this project exists. WinAppDriver searches a cached view of the
/// tree, which produces two long-standing defects: elements present on screen
/// but absent from the search (#857), and <c>FindElements</c> intermittently
/// returning nothing (#1079). Implementations of this interface query the live
/// tree so that class of failure has nowhere to occur.
/// </remarks>
public interface IElementFinder
{
    /// <summary>Finds every element matching a locator.</summary>
    /// <param name="searchRoot">The window to search within.</param>
    /// <param name="kind">What to match on.</param>
    /// <param name="value">The value to match.</param>
    /// <returns>The matching element ids, or why the search could not run.</returns>
    FindResult FindAll(nint searchRoot, LocatorKind kind, string value);
}

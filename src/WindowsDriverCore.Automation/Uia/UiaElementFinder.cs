using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using WindowsDriverCore.Automation.Locators;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// Finds elements by querying the live UI Automation tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>No retained result set.</b> Every call builds a fresh condition and asks
/// UIA to walk the tree now, because a set of matches kept between calls would
/// answer about a UI that has moved on.
/// </para>
/// <para>
/// An earlier version of this comment justified that by attributing WinAppDriver
/// #857 and #1079 to a cached view. <c>docs/FOUNDING-PREMISE.md</c> retracts both
/// descriptions, and the justification does not need them: it follows from what a
/// query is.
/// </para>
/// <para>
/// It costs a cross-process tree walk per find. If that shows up in a benchmark
/// the answers are <c>IUIAutomationCacheRequest</c>, to fetch more per round
/// trip, and holding the element itself for later commands — measured in
/// <c>HeldElementLivenessTests</c> to be a live proxy rather than a snapshot, and
/// therefore not the thing this paragraph rules out.
/// </para>
/// </remarks>
public sealed class UiaElementFinder : IElementFinder
{
    private readonly IUIAutomation _automation;

    /// <summary>Creates the finder.</summary>
    /// <param name="automation">The UI Automation root object.</param>
    /// <exception cref="ArgumentNullException"><paramref name="automation"/> is null.</exception>
    public UiaElementFinder(IUIAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = automation;
    }

    /// <inheritdoc />
    public FindResult FindAll(nint searchRoot, LocatorKind kind, string value) =>
        Find(searchRoot, kind, value, exhaustive: true);

    /// <inheritdoc />
    public FindResult FindFirst(nint searchRoot, LocatorKind kind, string value) =>
        Find(searchRoot, kind, value, exhaustive: false);

    /// <summary>
    /// One search, stopping at the first match or enumerating everything.
    /// </summary>
    /// <remarks>
    /// The two differ only in the UIA call. Everything around it — the XPath
    /// refusal, the window failure, the control-type lookup, the runtime-id
    /// filter — is identical, and duplicating it is how the singular and plural
    /// paths of WinAppDriver's own XPath drifted apart into issue #1079.
    /// </remarks>
    private FindResult Find(nint searchRoot, LocatorKind kind, string value, bool exhaustive)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (kind == LocatorKind.XPath)
        {
            // Not implemented. UIA has no XPath; WinAppDriver evaluates its own
            // over the tree, which is a piece of work in its own right. Reporting
            // every expression as invalid is wrong for valid ones, but it is
            // wrong loudly — the alternative, silently matching nothing, would
            // look like a correct search that found no elements.
            return FindResult.Failed(FindFailure.XPathLookupError);
        }

        IUIAutomationElement? root;
        try
        {
            root = _automation.ElementFromHandle(searchRoot);
        }
        catch (COMException)
        {
            // The window went away between the session check and here.
            return FindResult.Failed(FindFailure.NoSuchWindow);
        }

        if (root is null)
        {
            return FindResult.Failed(FindFailure.NoSuchWindow);
        }

        try
        {
            // RuntimeId cannot be used in a property condition — UIA rejects it
            // with E_INVALIDARG, which is not documented anywhere obvious and was
            // found by trying it. So a search by id enumerates and compares
            // instead. That costs a full descendant walk, which is the price of
            // holding no cache; the alternative is keeping elements between calls,
            // which is precisely the design that produces #857 and #1079.
            IUIAutomationCondition? built = kind == LocatorKind.RuntimeId
                ? _automation.CreateTrueCondition()
                : CreateCondition(kind, value);

            if (built is null)
            {
                // A tag name that is not a control type. Not an error: it is a
                // search that matched nothing, so POST /element answers "no such
                // element" and POST /elements answers an empty array — both of
                // which the routes already derive from an empty result.
                return FindResult.Matched([]);
            }

            using ComScope<IUIAutomationCondition> condition = new(built);

            // A search by element id has to enumerate whatever the condition
            // matched and compare, because UIA rejects RuntimeId in a property
            // condition — so stopping at the first match would stop at the wrong
            // element.
            bool mustEnumerate = exhaustive || kind == LocatorKind.RuntimeId;

            IReadOnlyList<string> ids = mustEnumerate
                ? ReadRuntimeIds(root.FindAll(TreeScope.TreeScope_Descendants, condition.Value))
                : ReadFirstRuntimeId(root.FindFirst(TreeScope.TreeScope_Descendants, condition.Value));

            return FindResult.Matched(
                kind == LocatorKind.RuntimeId
                    ? [.. ids.Where(id => string.Equals(id, value, StringComparison.Ordinal))]
                    : ids);
        }
        catch (COMException)
        {
            return FindResult.Failed(FindFailure.NoSuchWindow);
        }
    }

    /// <summary>
    /// The UIA condition for a locator, or <see langword="null"/> when the
    /// locator cannot match anything.
    /// </summary>
    /// <remarks>
    /// Null is reserved for one case: a <c>tag name</c> that is not a control
    /// type. That is user input rather than a defect, and it has to produce an
    /// empty find rather than an exception.
    /// </remarks>
    private IUIAutomationCondition? CreateCondition(LocatorKind kind, string value) => kind switch
    {
        LocatorKind.AutomationId =>
            _automation.CreatePropertyCondition(UiaPropertyIds.AutomationId, value),

        LocatorKind.ClassName =>
            _automation.CreatePropertyCondition(UiaPropertyIds.ClassName, value),

        LocatorKind.Name =>
            _automation.CreatePropertyCondition(UiaPropertyIds.Name, value),

        // ControlType, not LocalizedControlType. The two property ids differ by
        // one digit and this driver had the wrong one: 30004 is a localized
        // display string, 30003 is the id whose programmatic name the client
        // sends. See UiaControlTypes for the measurement that settled it.
        LocatorKind.ControlType => UiaControlTypes.TryGetId(value, out int controlTypeId)
            ? _automation.CreatePropertyCondition(UiaPropertyIds.ControlType, controlTypeId)
            : null,

        // XPath and RuntimeId are handled before this point; anything else is a
        // locator kind added without a condition, which is a bug rather than input.
        _ => throw new NotSupportedException($"No UIA condition for locator kind {kind}."),
    };

    private static List<string> ReadFirstRuntimeId(IUIAutomationElement? match)
    {
        if (match is null)
        {
            return [];
        }

        try
        {
            string? runtimeId = UiaRuntimeId.Read(match);

            return runtimeId is null ? [] : [runtimeId];
        }
        finally
        {
            Marshal.ReleaseComObject(match);
        }
    }

    private static List<string> ReadRuntimeIds(IUIAutomationElementArray? matches)
    {
        List<string> ids = [];
        if (matches is null)
        {
            return ids;
        }

        for (int index = 0; index < matches.Length; index++)
        {
            IUIAutomationElement? element = matches.GetElement(index);
            if (element is null)
            {
                continue;
            }

            try
            {
                string? runtimeId = UiaRuntimeId.Read(element);
                if (runtimeId is not null)
                {
                    ids.Add(runtimeId);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(element);
            }
        }

        return ids;
    }
}

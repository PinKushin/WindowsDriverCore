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
    private readonly IElementResolver _resolver;

    /// <summary>Creates the finder.</summary>
    /// <param name="automation">The UI Automation root object.</param>
    /// <param name="resolver">Resolves a container element for a nested search.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public UiaElementFinder(IUIAutomation automation, IElementResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(resolver);

        _automation = automation;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public FindResult FindAll(SearchScope scope, LocatorKind kind, string value) =>
        Find(scope, kind, value, exhaustive: true);

    /// <inheritdoc />
    public FindResult FindFirst(SearchScope scope, LocatorKind kind, string value) =>
        Find(scope, kind, value, exhaustive: false);

    /// <summary>
    /// One search, stopping at the first match or enumerating everything.
    /// </summary>
    /// <remarks>
    /// The two differ only in the UIA call. Everything around it — the XPath
    /// refusal, the window failure, the control-type lookup, the runtime-id
    /// filter — is identical, and duplicating it is how the singular and plural
    /// paths of WinAppDriver's own XPath drifted apart into issue #1079.
    /// </remarks>
    private FindResult Find(SearchScope scope, LocatorKind kind, string value, bool exhaustive)
    {
        ArgumentNullException.ThrowIfNull(value);

        // A nested search roots at the container element; everything else about
        // the search is identical, which is the point — the singular and plural
        // XPath paths of WinAppDriver drifted apart precisely because they were
        // written twice.
        using ElementLookupResult container = scope.ContainerElementId is null
            ? ElementLookupResult.Failed(ElementLookupOutcome.NotFound)
            : _resolver.Resolve(scope.Window, scope.ContainerElementId);

        if (scope.ContainerElementId is not null &&
            container.Outcome != ElementLookupOutcome.Resolved)
        {
            // A container that cannot be resolved is a FAILURE, not a find of
            // nothing. The two were the same answer here, and it cost two suite
            // tests: FindNestedElementError_StaleElement wants the stale sentence
            // and got "could not be located", and FindNestedElementsError_
            // StaleElement got an empty array and no exception at all - because
            // an empty array is exactly what a live container holding nothing
            // returns.
            //
            // WHICH fault it becomes is not decided here. This layer does not
            // know which ids this server has handed out, and stale-versus-unknown
            // turns on precisely that; the protocol layer answers it with the
            // registry, the same way every other element command does.
            return container.Outcome == ElementLookupOutcome.NoSuchWindow
                ? FindResult.Failed(FindFailure.NoSuchWindow)
                : FindResult.Failed(FindFailure.NoSuchContainer);
        }

        IUIAutomationElement? root;
        try
        {
            root = container.Element ?? _automation.ElementFromHandle(scope.Window);
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

        if (kind == LocatorKind.XPath)
        {
            // Rooted at whatever the scope resolved to, so a nested XPath find
            // searches inside the container like every other locator rather than
            // silently restarting at the window.
            return EvaluateXPath(root, value);
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

            // A SEARCH INCLUDES THE ELEMENT IT STARTS FROM, whether that is a
            // container or the window.
            //
            // Measured first for the NESTED case, from the compatibility suite:
            // it searches the alarm tab for the alarm tab's own automation id and
            // asserts the answer is one element and that it IS the container -
            // twice, once by automation id and once by runtime id. Both answered
            // zero.
            //
            // The window scope was deliberately left on TreeScope_Descendants at
            // that point, with the note that nothing measured said a window
            // search should match the window, and that widening the scope every
            // find in the driver runs under was not a change to make on the
            // strength of a nested measurement. That was right, and the
            // measurement has since arrived.
            //
            // GetElementSize does session.FindElementByClassName(
            // "ApplicationFrameWindow") - the session root's OWN class - and
            // asserts its size equals the window's. WinAppDriver passes it; this
            // answered zero, because Descendants excludes the element you start
            // from. So the two scopes were never really different rules, only
            // one rule with half the evidence.
            //
            // THE RISK IS THAT THIS IS NOT A LOCAL CHANGE. Every find runs at
            // window scope, so a root that starts intercepting matches would
            // show up everywhere. That is what the full suite is for, and
            // WindowScopedFindMatchesTheWindowTests carries the named controls:
            // a descendant find must still answer with the descendant, and an
            // absent locator must still find nothing.
            TreeScope treeScope = TreeScope.TreeScope_Subtree;

            IReadOnlyList<string> ids = mustEnumerate
                ? ReadRuntimeIds(root.FindAll(treeScope, condition.Value))
                : ReadFirstRuntimeId(root.FindFirst(treeScope, condition.Value));

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

    /// <summary>Evaluates an XPath expression and returns the matched ids.</summary>
    /// <param name="root">The subtree to search.</param>
    /// <param name="expression">The expression.</param>
    /// <returns>The matched element ids, or an XPath lookup failure.</returns>
    /// <remarks>
    /// Runtime ids are read only for the MATCHES. RuntimeId cannot be cached, so
    /// each one costs a crossing — but the matched set is small where the
    /// projected tree is not.
    /// </remarks>
    private FindResult EvaluateXPath(IUIAutomationElement root, string expression)
    {
        UiaXPathEvaluator evaluator = new(_automation);

        if (!evaluator.TrySelect(root, expression, out List<IUIAutomationElement> matched))
        {
            return FindResult.Failed(FindFailure.XPathLookupError);
        }

        List<string> ids = [];

        try
        {
            foreach (IUIAutomationElement element in matched)
            {
                string? id = UiaRuntimeId.Read(element);
                if (id is not null)
                {
                    ids.Add(id);
                }
            }
        }
        finally
        {
            foreach (IUIAutomationElement element in matched)
            {
                Marshal.ReleaseComObject(element);
            }
        }

        return FindResult.Matched(ids);
    }

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

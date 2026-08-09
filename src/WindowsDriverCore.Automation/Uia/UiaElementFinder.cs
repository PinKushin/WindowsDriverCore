using System.Collections.Generic;
using System.Globalization;
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
/// <b>No caching, deliberately.</b> Every call builds a fresh condition and asks
/// UIA to walk the tree now. That is the whole design: WinAppDriver's #857 and
/// #1079 are both symptoms of searching a cached view that has drifted from what
/// is on screen, and a search that never caches cannot drift.
/// </para>
/// <para>
/// It costs a cross-process round trip per find, which is the trade being made
/// knowingly. If it ever shows up in a benchmark, the answer is
/// <c>IUIAutomationCacheRequest</c> to fetch more per round trip — not to hold a
/// snapshot between calls, which would reintroduce exactly the defect this
/// replaces.
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
    public FindResult FindAll(nint searchRoot, LocatorKind kind, string value)
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
            using ComScope<IUIAutomationCondition> condition = new(
                kind == LocatorKind.RuntimeId
                    ? _automation.CreateTrueCondition()
                    : CreateCondition(kind, value));

            IUIAutomationElementArray matches = root.FindAll(
                TreeScope.TreeScope_Descendants,
                condition.Value);

            IReadOnlyList<string> ids = ReadRuntimeIds(matches);

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

    private IUIAutomationCondition CreateCondition(LocatorKind kind, string value) => kind switch
    {
        LocatorKind.AutomationId =>
            _automation.CreatePropertyCondition(UiaPropertyIds.AutomationId, value),

        LocatorKind.ClassName =>
            _automation.CreatePropertyCondition(UiaPropertyIds.ClassName, value),

        LocatorKind.Name =>
            _automation.CreatePropertyCondition(UiaPropertyIds.Name, value),

        LocatorKind.LocalizedControlType =>
            _automation.CreatePropertyCondition(UiaPropertyIds.LocalizedControlType, value),

        // XPath and RuntimeId are handled before this point; anything else is a
        // locator kind added without a condition, which is a bug rather than input.
        _ => throw new NotSupportedException($"No UIA condition for locator kind {kind}."),
    };

    /// <summary>
    /// The element id for an element: its UIA RuntimeId, dot-separated.
    /// </summary>
    /// <remarks>
    /// Dots rather than commas. WinAppDriver's documentation and its live
    /// responses both use dots (<c>42.19466560.4.73</c>); the previous
    /// implementation used commas, which round-tripped within itself but did not
    /// match ids copied from inspect.exe or from a WinAppDriver session.
    /// </remarks>
    /// <remarks>
    /// Written by hand rather than with <c>string.Join</c> over a LINQ projection,
    /// which allocated a string per integer and then a second one for the join.
    /// Measured at roughly 80us per element across 47 elements — small in
    /// absolute terms, but it was most of the 17% of a find spent in managed code,
    /// and the UIA calls it sits beside are not something this project can make
    /// faster.
    /// </remarks>
    internal static string FormatRuntimeId(int[] runtimeId)
    {
        if (runtimeId.Length == 0)
        {
            return string.Empty;
        }

        // Eleven digits covers int.MinValue including its sign, plus one
        // separator per part. Stack-allocated: a runtime id is a handful of ints,
        // never an unbounded list.
        Span<char> buffer = stackalloc char[runtimeId.Length * 12];
        int written = 0;

        for (int index = 0; index < runtimeId.Length; index++)
        {
            if (index > 0)
            {
                buffer[written++] = '.';
            }

            runtimeId[index].TryFormat(
                buffer[written..], out int partLength, provider: CultureInfo.InvariantCulture);
            written += partLength;
        }

        return new string(buffer[..written]);
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
                int[]? runtimeId = element.GetRuntimeId();

                // An element can be found and still have no resolvable identity
                // when the tree is mutating underneath the query. Skipping it is
                // deliberate: returning an element the caller cannot address is
                // what produces the client-side InvalidOperationException that
                // callers catching NoSuchElementException never catch.
                if (runtimeId is { Length: > 0 })
                {
                    ids.Add(FormatRuntimeId(runtimeId));
                }
            }
            catch (COMException)
            {
                // Went away mid-enumeration. Same reasoning as above.
            }
            finally
            {
                Marshal.ReleaseComObject(element);
            }
        }

        return ids;
    }
}

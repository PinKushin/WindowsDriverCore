using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <inheritdoc cref="IElementResolver" />
public sealed class UiaElementResolver : IElementResolver
{
    private readonly IUIAutomation _automation;

    /// <summary>Creates the resolver.</summary>
    /// <param name="automation">The UI Automation root object.</param>
    /// <exception cref="ArgumentNullException"><paramref name="automation"/> is null.</exception>
    public UiaElementResolver(IUIAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = automation;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="elementId"/> is null.</exception>
    /// <remarks>
    /// A full descendant walk, because UIA rejects RuntimeId in a property
    /// condition with <c>E_INVALIDARG</c> — undocumented, and found by trying it.
    /// So there is no way to ask "give me the element with this id" and the
    /// answer has to be found by comparison.
    ///
    /// It matches <see cref="UiaElementFinder"/>'s scope exactly, deliberately:
    /// an id the finder can issue is an id this can resolve, and the two walking
    /// different subtrees would produce ids that work once and never again.
    /// </remarks>
    public ElementLookupResult Resolve(nint searchRoot, string elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);

        IUIAutomationElement? root;
        try
        {
            root = _automation.ElementFromHandle(searchRoot);
        }
        catch (COMException)
        {
            return ElementLookupResult.Failed(ElementLookupOutcome.NoSuchWindow);
        }

        if (root is null)
        {
            return ElementLookupResult.Failed(ElementLookupOutcome.NoSuchWindow);
        }

        try
        {
            using ComScope<IUIAutomationCondition> condition = new(_automation.CreateTrueCondition());

            // SUBTREE, SO THE WINDOW CAN RESOLVE ITSELF. Descendants excludes
            // the element the walk starts from, so an id belonging to the window
            // resolved as NotFound - and a client that had just been GIVEN that
            // id by a find could not then use it.
            //
            // Caught by WindowScopedFindMatchesTheWindowTests after the FIND was
            // widened. Widening only the find would have looked correct and
            // still failed the suite's GetElementSize, which finds
            // ApplicationFrameWindow and then reads its Size - two calls, and
            // only the first was fixed.
            IUIAutomationElementArray? matches = root.FindAll(
                TreeScope.TreeScope_Subtree,
                condition.Value);

            return matches is null
                ? ElementLookupResult.Failed(ElementLookupOutcome.NotFound)
                : FirstWithId(matches, elementId);
        }
        catch (COMException)
        {
            // The window went away mid-walk.
            return ElementLookupResult.Failed(ElementLookupOutcome.NoSuchWindow);
        }
    }

    private static ElementLookupResult FirstWithId(IUIAutomationElementArray matches, string elementId)
    {
        for (int index = 0; index < matches.Length; index++)
        {
            IUIAutomationElement? element = matches.GetElement(index);
            if (element is null)
            {
                continue;
            }

            // The match is handed to the caller, so it is not released here —
            // every other element in the array is.
            if (string.Equals(UiaRuntimeId.Read(element), elementId, StringComparison.Ordinal))
            {
                return ElementLookupResult.Resolved(element);
            }

            Marshal.ReleaseComObject(element);
        }

        return ElementLookupResult.Failed(ElementLookupOutcome.NotFound);
    }
}

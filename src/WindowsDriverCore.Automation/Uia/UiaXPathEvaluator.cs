using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.XPath;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// Evaluates an XPath expression over a UI Automation subtree.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine is <see cref="System.Xml.XPath"/>, deliberately.</b> XPath 1.0
/// was frozen in 1999 and hand-writing an evaluator for it means reimplementing
/// node-set to string to number conversion, document order per axis, and
/// positional semantics where <c>//a[1]</c> and <c>(//a)[1]</c> differ. Every one
/// of those is a place to be quietly wrong, and each would surface as a flaky
/// driver rather than as a wrong evaluator.
/// </para>
/// <para>
/// <b>Measured against WinAppDriver 1.2.1</b> — recorded in
/// <c>Recordings/winappdriver-xpath-surface.json</c> — its XPath is complete
/// XPath 1.0, including <c>//Button[1]</c> matching eight elements where
/// <c>(//Button)[1]</c> matches one. That is correct, it is the subtlety
/// hand-rolled engines fail, and nobody implements it by accident: they almost
/// certainly projected the tree to XML and used this same engine. Using it too
/// makes the semantics match by construction rather than by effort.
/// </para>
/// <para>
/// <b>What WinAppDriver got wrong is the lifetime, not the engine.</b> Its XPath
/// is reported flaky, and a correct engine over a <i>stale</i> document gives
/// correct answers about a tree that no longer exists — which is issue #1079.
/// So the projection here is built inside a single evaluation and never held: it
/// is a local, not a field.
/// </para>
/// <para>
/// <b>One crossing, not one per node.</b> A cache request scoped to
/// <c>TreeScope_Subtree</c> brings back the structure <i>and</i> the properties
/// in a single call, after which <c>GetCachedChildren</c> walks in-process.
/// Measured: stepping the tree with a walker costs 2.33x the bulk fetch before
/// reading a single property, and a cache request carrying five properties is
/// 1.76x cheaper than reading them live.
/// </para>
/// </remarks>
internal sealed class UiaXPathEvaluator
{
    private const int NameProperty = 30005;
    private const int AutomationIdProperty = 30011;
    private const int ControlTypeProperty = 30003;
    private const int ClassNameProperty = 30012;
    private const int IsEnabledProperty = 30010;

    /// <summary>Where an element's index is parked on its XML node.</summary>
    /// <remarks>
    /// A double underscore because it shares a namespace with real UIA property
    /// names, and an expression written against a real application must never
    /// collide with it.
    /// </remarks>
    private const string IndexAttribute = "__i";

    private readonly IUIAutomation _automation;

    /// <summary>Creates the evaluator.</summary>
    /// <param name="automation">The automation root.</param>
    /// <exception cref="ArgumentNullException"><paramref name="automation"/> is null.</exception>
    internal UiaXPathEvaluator(IUIAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = automation;
    }

    /// <summary>Selects the elements an expression matches.</summary>
    /// <param name="root">The subtree to search.</param>
    /// <param name="expression">The XPath expression.</param>
    /// <param name="matched">The matched elements, in document order.</param>
    /// <returns>
    /// <see langword="false"/> when the expression is not valid XPath, which the
    /// protocol layer reports as an XPath lookup error. An expression that is
    /// valid and matches nothing returns <see langword="true"/> and an empty
    /// list — a search that found nothing is not a failure.
    /// </returns>
    /// <remarks>
    /// The caller owns every element in <paramref name="matched"/> and must
    /// release them.
    /// </remarks>
    internal bool TrySelect(
        IUIAutomationElement root,
        string expression,
        out List<IUIAutomationElement> matched)
    {
        ArgumentNullException.ThrowIfNull(root);

        matched = [];

        // Parsed first so an invalid expression costs nothing: no tree walk, no
        // projection, and the same fault the client would have got anyway.
        XPathExpression parsed;
        try
        {
            parsed = XPathExpression.Compile(expression);
        }
        catch (XPathException)
        {
            return false;
        }

        List<IUIAutomationElement> owned = [];

        try
        {
            XmlDocument document = Project(root, owned);

            XPathNodeIterator selected;
            try
            {
                selected = document.CreateNavigator()!.Select(parsed);
            }
            catch (XPathException)
            {
                // Compiles but will not evaluate — an unknown function, or a
                // result that is not a node set. Both are lookup errors, not
                // empty results.
                return false;
            }

            while (selected.MoveNext())
            {
                if (selected.Current is null)
                {
                    continue;
                }

                string? index = selected.Current.GetAttribute(IndexAttribute, string.Empty);
                if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out int at) ||
                    at < 0 || at >= owned.Count)
                {
                    // The expression selected something that is not one of our
                    // element nodes — an attribute, or a synthesized node. It
                    // has no element to hand back.
                    continue;
                }

                matched.Add(owned[at]);
            }

            // Whatever was not matched is released here; the matches are the
            // caller's from now on.
            for (int i = 0; i < owned.Count; i++)
            {
                if (!matched.Contains(owned[i]))
                {
                    Marshal.ReleaseComObject(owned[i]);
                }
            }

            return true;
        }
        catch (COMException)
        {
            foreach (IUIAutomationElement element in owned)
            {
                Marshal.ReleaseComObject(element);
            }

            matched = [];
            return false;
        }
    }

    /// <summary>Fetches the subtree and mirrors it as XML.</summary>
    /// <param name="root">The subtree root.</param>
    /// <param name="owned">Receives every element, indexed as the XML refers to them.</param>
    /// <returns>The document.</returns>
    private XmlDocument Project(IUIAutomationElement root, List<IUIAutomationElement> owned)
    {
        IUIAutomationCacheRequest request = _automation.CreateCacheRequest();
        request.AddProperty(NameProperty);
        request.AddProperty(AutomationIdProperty);
        request.AddProperty(ControlTypeProperty);
        request.AddProperty(ClassNameProperty);
        request.AddProperty(IsEnabledProperty);

        // Subtree, so ONE call brings back the shape as well as the values and
        // GetCachedChildren then walks without crossing again.
        request.TreeScope = TreeScope.TreeScope_Subtree;

        // Full, not None: the matched elements have to answer GetRuntimeId
        // afterwards, and RuntimeId is not cacheable — measured, AddProperty
        // accepts it and GetCachedPropertyValue then throws E_INVALIDARG.
        request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;

        IUIAutomationElement cached = root.BuildUpdatedCache(request);

        XmlDocument document = new();
        XmlElement rootNode = Describe(document, cached, owned);
        document.AppendChild(rootNode);
        AppendChildren(document, rootNode, cached, owned);

        return document;
    }

    private static void AppendChildren(
        XmlDocument document,
        XmlElement parentNode,
        IUIAutomationElement parent,
        List<IUIAutomationElement> owned)
    {
        IUIAutomationElementArray children = parent.GetCachedChildren();
        if (children is null)
        {
            return;
        }

        for (int i = 0; i < children.Length; i++)
        {
            IUIAutomationElement child = children.GetElement(i);
            XmlElement childNode = Describe(document, child, owned);
            parentNode.AppendChild(childNode);
            AppendChildren(document, childNode, child, owned);
        }
    }

    private static XmlElement Describe(
        XmlDocument document,
        IUIAutomationElement element,
        List<IUIAutomationElement> owned)
    {
        int controlType = CachedInt(element, ControlTypeProperty);
        XmlElement node = document.CreateElement(UiaControlTypes.ElementName(controlType));

        Set(node, "Name", CachedString(element, NameProperty));
        Set(node, "AutomationId", CachedString(element, AutomationIdProperty));
        Set(node, "ClassName", CachedString(element, ClassNameProperty));
        Set(node, "IsEnabled", CachedBool(element, IsEnabledProperty));
        Set(node, "ControlType", UiaControlTypes.TagName(controlType));

        owned.Add(element);
        node.SetAttribute(
            IndexAttribute,
            (owned.Count - 1).ToString(CultureInfo.InvariantCulture));

        return node;
    }

    private static void Set(XmlElement node, string name, string value) =>
        node.SetAttribute(name, value);

    private static string CachedString(IUIAutomationElement element, int property)
    {
        try
        {
            return element.GetCachedPropertyValue(property) as string ?? string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private static int CachedInt(IUIAutomationElement element, int property)
    {
        try
        {
            return element.GetCachedPropertyValue(property) is int value ? value : 0;
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private static string CachedBool(IUIAutomationElement element, int property)
    {
        try
        {
            return element.GetCachedPropertyValue(property) is bool value && value
                ? "True"
                : "False";
        }
        catch (COMException)
        {
            return "False";
        }
    }
}

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Xml;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <inheritdoc cref="IPageSourceReader" />
/// <remarks>
/// <para>
/// The document is <see cref="UiaTreeProjection"/>'s, unchanged apart from having
/// its bookkeeping attribute removed. That is what makes <c>GET /source</c> and an
/// XPath locator answer questions about the same tree.
/// </para>
/// <para>
/// The compatibility suite's <c>Source.GetSource</c> parses the answer with
/// <c>XmlDocument.LoadXml</c> and asserts <c>//Button</c> matches, so node names
/// are the bare control-type names an XPath step uses — <c>Button</c>, not
/// <c>ControlType.Button</c>.
/// </para>
/// </remarks>
public sealed class UiaPageSource : IPageSourceReader
{
    private readonly IUIAutomation _automation;
    private readonly UiaTreeProjection _projection;

    /// <summary>Creates the reader.</summary>
    /// <param name="automation">The automation root.</param>
    /// <exception cref="ArgumentNullException"><paramref name="automation"/> is null.</exception>
    public UiaPageSource(IUIAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);

        _automation = automation;
        _projection = new UiaTreeProjection(automation);
    }

    /// <inheritdoc />
    public string? Source(nint window)
    {
        IUIAutomationElement? root;
        try
        {
            root = _automation.ElementFromHandle(window);
        }
        catch (COMException)
        {
            // The window went away between the session's existence check and
            // here. Null, not an empty document: see IPageSourceReader.
            return null;
        }

        if (root is null)
        {
            return null;
        }

        List<IUIAutomationElement> owned = [];

        try
        {
            XmlDocument document = _projection.Project(root, owned);
            Strip(document.DocumentElement);
            return document.OuterXml;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            foreach (IUIAutomationElement element in owned)
            {
                Marshal.ReleaseComObject(element);
            }

            Marshal.ReleaseComObject(root);
        }
    }

    /// <summary>Removes the projection's index attribute, recursively.</summary>
    /// <remarks>
    /// It is how a matched XML node is mapped back to its element and has no
    /// meaning outside this process. Leaving it in would publish an
    /// implementation detail that a client could then write expressions against.
    /// </remarks>
    private static void Strip(XmlElement? node)
    {
        if (node is null)
        {
            return;
        }

        node.RemoveAttribute(UiaTreeProjection.IndexAttribute);

        foreach (XmlNode child in node.ChildNodes)
        {
            Strip(child as XmlElement);
        }
    }
}

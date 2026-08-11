namespace WindowsDriverCore.Automation;

/// <summary>
/// Renders a window's UI Automation subtree as XML.
/// </summary>
/// <remarks>
/// <para>
/// This is the same projection XPath evaluates against, and deliberately so. A
/// client that reads <c>GET /source</c>, picks a node out of it and then searches
/// for that node with an XPath locator must get the element it saw; two
/// independent renderings would let the two disagree, and the disagreement would
/// surface as an XPath expression that is demonstrably correct against the source
/// the driver just handed over and still matches nothing.
/// </para>
/// <para>
/// Narrow on purpose. Page source has nothing to do with reading an element's
/// properties or acting on one, and a consumer of this should not be handed
/// either.
/// </para>
/// </remarks>
public interface IPageSourceReader
{
    /// <summary>The window's subtree, as XML.</summary>
    /// <param name="window">The session's window.</param>
    /// <returns>
    /// The document, or <see langword="null"/> when the window no longer exists.
    /// Null rather than an empty string, because an empty document is a
    /// meaningful answer for a window with nothing in it and must not be confused
    /// with a window that has gone.
    /// </returns>
    string? Source(nint window);
}

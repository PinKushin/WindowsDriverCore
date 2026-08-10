namespace WindowsDriverCore.Automation;

// Split out of IElementResolver.cs during the Contracts extraction. It sat
// beside IElementResolver and ElementLookupResult, both of which expose
// IUIAutomationElement and therefore cannot leave the Windows-targeted
// assembly. This one is nint in, void out — nothing platform-specific — and
// Protocol needs it to drop a session's cached handles.
/// <summary>
/// Releases element handles a driver is holding.
/// </summary>
/// <remarks>
/// Not a cref: IElementResolver lives in the Automation assembly, which
/// Contracts cannot reference — this project is the bottom of the graph and
/// depends on nothing.
///
/// Separate from IElementResolver because most resolvers hold
/// nothing and should not be asked to implement this. The protocol layer calls
/// it when a session ends: every handle keeps a provider object alive inside the
/// application under test, and a driver that outlives many sessions would
/// otherwise pin objects in applications that have closed.
/// </remarks>
public interface IElementHandleCache
{
    /// <summary>Releases every handle held for a window.</summary>
    /// <param name="searchRoot">The window whose session is ending.</param>
    void Forget(nint searchRoot);
}

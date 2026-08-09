using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// Keeps the elements it has resolved, so a second command on the same element
/// does not walk the tree again.
/// </summary>
/// <remarks>
/// <para>
/// A decorator over the real resolver, and <b>purely an optimisation</b>: every
/// miss, every eviction and every invalidation falls through to the walk, which
/// is still correct on its own. Nothing about the answer depends on whether an
/// entry was cached.
/// </para>
/// <para>
/// <b>Why this is not the design the project rejected.</b> An
/// <c>IUIAutomationElement</c> obtained without a cache request is a live proxy,
/// not a snapshot: property reads cross to the provider every time, the runtime
/// id survives changes to the tree, and a destroyed element throws
/// <c>UIA_E_ELEMENTNOTAVAILABLE</c> rather than answering with the last value it
/// saw. All three are measured in <c>HeldElementLivenessTests</c>. What does go
/// stale — a retained result set, or properties fetched through
/// <c>FindAllBuildCache</c> — is not kept here.
/// </para>
/// <para>
/// <b>What is verified on every hit.</b> The handle's runtime id is read and
/// compared against the id asked for. That costs roughly 5 microseconds against
/// a tree walk of roughly 11 milliseconds, and it closes the one hole worth
/// worrying about: a handle that has quietly stopped naming the element the
/// client means. If it throws or disagrees, the entry is released and the walk
/// runs.
/// </para>
/// <para>
/// <b>Bounded, because each entry keeps a provider object alive in the target
/// application.</b> Least-recently-used eviction at a fixed cap, releasing the
/// COM object as it goes. An unbounded table would be the previous
/// implementation's leak with a better justification.
/// </para>
/// </remarks>
public sealed class CachingElementResolver : IElementResolver, IElementHandleCache, IDisposable
{
    /// <summary>How many handles are kept before the oldest is released.</summary>
    /// <remarks>
    /// A suite works with a working set far smaller than this between finds.
    /// The number is a guess at a safe ceiling rather than a measured optimum,
    /// and the cost of it being wrong is bounded in both directions: too low
    /// costs a tree walk, too high costs memory in the application under test.
    /// </remarks>
    public const int Capacity = 256;

    private readonly IElementResolver _inner;
    private readonly Lock _gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _recency = new();

    private bool _disposed;

    /// <summary>Creates the caching resolver.</summary>
    /// <param name="inner">The resolver that actually walks the tree.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public CachingElementResolver(IElementResolver inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>How many handles are currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="elementId"/> is null.</exception>
    public ElementLookupResult Resolve(nint searchRoot, string elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);

        CacheKey key = new(searchRoot, elementId);

        if (TryBorrow(key, out IUIAutomationElement? cached) && cached is not null)
        {
            return ElementLookupResult.Borrowed(cached);
        }

        ElementLookupResult resolved = _inner.Resolve(searchRoot, elementId);

        if (resolved.Outcome != ElementLookupOutcome.Resolved ||
            resolved.Element is not IUIAutomationElement element)
        {
            return resolved;
        }

        // The cache takes over the lifetime the inner resolver handed out, so
        // the result the caller gets is borrowed and disposing it releases
        // nothing.
        Store(key, element);

        return ElementLookupResult.Borrowed(element);
    }

    /// <inheritdoc />
    public void Forget(nint searchRoot)
    {
        lock (_gate)
        {
            LinkedListNode<CacheEntry>? node = _recency.First;

            while (node is not null)
            {
                LinkedListNode<CacheEntry> current = node;
                node = node.Next;

                if (current.Value.Key.SearchRoot == searchRoot)
                {
                    Remove(current);
                }
            }
        }
    }

    /// <summary>Releases every handle.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            while (_recency.First is LinkedListNode<CacheEntry> first)
            {
                Remove(first);
            }
        }
    }

    private bool TryBorrow(CacheKey key, out IUIAutomationElement? element)
    {
        lock (_gate)
        {
            element = null;

            if (!_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                return false;
            }

            // The identity check. A handle is only usable if it still names the
            // element the caller asked for; anything else — a destroyed element,
            // a provider that has gone away — evicts and falls through to the
            // walk, which reaches the right answer on its own.
            string? currentId;
            try
            {
                currentId = UiaRuntimeId.Read(node.Value.Element);
            }
            catch (COMException)
            {
                Remove(node);
                return false;
            }

            if (!string.Equals(currentId, key.ElementId, StringComparison.Ordinal))
            {
                Remove(node);
                return false;
            }

            _recency.Remove(node);
            _recency.AddLast(node);
            element = node.Value.Element;

            return true;
        }
    }

    private void Store(CacheKey key, IUIAutomationElement element)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                // Racing a shutdown. Releasing here rather than storing keeps the
                // promise that Dispose released everything.
                Release(element);
                return;
            }

            if (_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                Remove(existing);
            }

            LinkedListNode<CacheEntry> node = _recency.AddLast(new CacheEntry(key, element));
            _entries[key] = node;

            while (_entries.Count > Capacity && _recency.First is LinkedListNode<CacheEntry> oldest)
            {
                Remove(oldest);
            }
        }
    }

    private void Remove(LinkedListNode<CacheEntry> node)
    {
        _entries.Remove(node.Value.Key);
        _recency.Remove(node);
        Release(node.Value.Element);
    }

    /// <summary>Releases an element, if it is something COM owns.</summary>
    /// <remarks>
    /// <c>ReleaseComObject</c> throws <c>ArgumentException</c> for anything that
    /// is not a runtime callable wrapper. This class is public and takes any
    /// <see cref="IElementResolver"/>, so an implementation that hands back a
    /// managed <c>IUIAutomationElement</c> is legal and must not crash the
    /// eviction path. It also makes the cache's own behaviour testable without
    /// driving a real application, which is how the eviction path got covered at
    /// all.
    /// </remarks>
    private static void Release(IUIAutomationElement element)
    {
        if (Marshal.IsComObject(element))
        {
            Marshal.ReleaseComObject(element);
        }
    }

    private readonly record struct CacheKey(nint SearchRoot, string ElementId);

    private sealed record CacheEntry(CacheKey Key, IUIAutomationElement Element);
}

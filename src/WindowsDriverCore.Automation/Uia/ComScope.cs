using System.Runtime.InteropServices;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// Releases a COM object at the end of a scope.
/// </summary>
/// <typeparam name="T">The COM interface type.</typeparam>
/// <remarks>
/// COM objects are reference counted and the runtime's finalizer-based release
/// is non-deterministic. The implementation being replaced stored elements in a
/// dictionary and never released any of them, leaking a runtime callable wrapper
/// on every repeated find.
///
/// Only for objects this code creates and owns — conditions, and elements pulled
/// out of an array. An object handed to a caller is the caller's to release.
/// </remarks>
internal readonly struct ComScope<T> : IDisposable
    where T : class
{
    /// <summary>Creates the scope.</summary>
    /// <param name="value">The COM object to own.</param>
    internal ComScope(T value) => Value = value;

    /// <summary>The COM object.</summary>
    internal T Value { get; }

    /// <summary>Releases the COM object.</summary>
    public void Dispose()
    {
        if (Value is not null)
        {
            Marshal.ReleaseComObject(Value);
        }
    }
}

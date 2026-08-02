using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Raw;

/// <summary>
/// Managed wrapper around IUIAutomationCondition COM object.
/// </summary>
public sealed class RawCondition : IDisposable
{
    private IUIAutomationCondition? _condition;
    private bool _disposed;

    public RawCondition(IUIAutomationCondition condition)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    public IUIAutomationCondition Condition =>
        _condition ?? throw new ObjectDisposedException(nameof(RawCondition));

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_condition is not null)
            {
                try { Marshal.ReleaseComObject(_condition); } catch { }
                _condition = null;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

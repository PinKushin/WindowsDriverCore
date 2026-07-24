using System.Collections.Concurrent;
using System.Windows.Automation;

namespace WindowsDriverCore.Automation;

public class ElementStore
{
    private readonly ConcurrentDictionary<string, AutomationElement> _elements = new();

    public string Store(AutomationElement element)
    {
        var runtimeId = element.GetRuntimeId();
        var elementId = string.Join(",", runtimeId);
        _elements[elementId] = element;
        return elementId;
    }

    public AutomationElement? Get(string elementId)
    {
        _elements.TryGetValue(elementId, out var element);
        return element;
    }

    public void Remove(string elementId)
    {
        _elements.TryRemove(elementId, out _);
    }

    public void Clear()
    {
        _elements.Clear();
    }
}

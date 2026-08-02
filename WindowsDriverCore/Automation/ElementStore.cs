using System.Collections.Concurrent;
using WindowsDriverCore.Automation.Raw;

namespace WindowsDriverCore.Automation;

public class ElementStore
{
    private readonly ConcurrentDictionary<string, RawAutomationElement> _elements = new();

    public string Store(RawAutomationElement element)
    {
        var elementId = element.GetRuntimeIdString();
        _elements[elementId] = element;
        return elementId;
    }

    public RawAutomationElement? Get(string elementId)
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

namespace WindowsDriverCore.Automation;

public interface IElementInteractor
{
    void Click(string elementId);
    void SendKeys(string elementId, string text);
    string GetText(string elementId);
    bool GetEnabled(string elementId);
    bool GetDisplayed(string elementId);
    string GetTagName(string elementId);
    string? GetAttribute(string elementId, string attributeName);
    void Clear(string elementId);
    string GetSelected(string elementId);
    string GetCoordinates(string elementId);
    string GetSize(string elementId);
    string GetLocationInView(string elementId);
    void ClickAt(string elementId, int x, int y);
    string GetElementId(string elementId);
}

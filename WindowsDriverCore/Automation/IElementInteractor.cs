namespace WindowsDriverCore.Automation;

public interface IElementInteractor
{
    void Click(IntPtr elementHandle);
    void SendKeys(IntPtr elementHandle, string text);
    string GetText(IntPtr elementHandle);
    bool GetEnabled(IntPtr elementHandle);
    bool GetDisplayed(IntPtr elementHandle);
}

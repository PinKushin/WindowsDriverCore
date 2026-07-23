namespace WindowsDriverCore.Automation;

public interface IElementFinder
{
    string FindElement(IntPtr windowHandle, string usingStrategy, string value);
    string[] FindElements(IntPtr windowHandle, string usingStrategy, string value);
}

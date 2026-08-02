using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Com;

public static class UIAutomationFactory
{
    private static IUIAutomation? _instance;

    public static IUIAutomation Create()
    {
        if (_instance is not null)
            return _instance;

        var comObj = new CUIAutomationClass();
        _instance = (IUIAutomation)comObj;
        return _instance;
    }
}

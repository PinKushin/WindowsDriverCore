using Interop.UIAutomationClient;
using WindowsDriverCore.Automation.Com;

namespace WindowsDriverCore.Automation.Raw;

/// <summary>
/// Creates UIA conditions from the IUIAutomation factory.
/// </summary>
public static class ConditionFactory
{
    private static IUIAutomation? _automation;

    public static void Initialize(IUIAutomation automation)
    {
        _automation = automation;
    }

    public static RawCondition CreatePropertyCondition(int propertyId, string value)
    {
        EnsureInitialized();
        var cond = _automation!.CreatePropertyCondition(propertyId, value);
        return new RawCondition(cond);
    }

    public static RawCondition CreatePropertyCondition(int propertyId, int value)
    {
        EnsureInitialized();
        var cond = _automation!.CreatePropertyCondition(propertyId, value);
        return new RawCondition(cond);
    }

    public static RawCondition CreatePropertyCondition(int propertyId, bool value)
    {
        EnsureInitialized();
        var cond = _automation!.CreatePropertyCondition(propertyId, value);
        return new RawCondition(cond);
    }

    public static RawCondition CreateAndCondition(RawCondition cond1, RawCondition cond2)
    {
        EnsureInitialized();
        var cond = _automation!.CreateAndCondition(cond1.Condition, cond2.Condition);
        return new RawCondition(cond);
    }

    public static RawCondition CreateOrCondition(RawCondition cond1, RawCondition cond2)
    {
        EnsureInitialized();
        var cond = _automation!.CreateOrCondition(cond1.Condition, cond2.Condition);
        return new RawCondition(cond);
    }

    public static RawCondition CreateNotCondition(RawCondition condition)
    {
        EnsureInitialized();
        var cond = _automation!.CreateNotCondition(condition.Condition);
        return new RawCondition(cond);
    }

    public static RawCondition CreateTrueCondition()
    {
        EnsureInitialized();
        var cond = _automation!.CreateTrueCondition();
        return new RawCondition(cond);
    }

    private static void EnsureInitialized()
    {
        if (_automation is null)
            throw new InvalidOperationException("ConditionFactory not initialized. Call Initialize() first.");
    }
}

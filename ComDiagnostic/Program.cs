using Interop.UIAutomationClient;

Console.WriteLine("=== Interop.UIAutomationClient Test ===");

Console.Write("1. Creating CUIAutomationClass... ");
var comObj = new CUIAutomationClass();
Console.WriteLine($"OK");

Console.Write("2. Cast to IUIAutomation... ");
IUIAutomation automation = (IUIAutomation)comObj;
Console.WriteLine($"OK");

Console.Write("3. GetRootElement... ");
IUIAutomationElement root = automation.GetRootElement();
Console.WriteLine($"OK type={root.GetType().Name}");

Console.Write("4. Root Name... ");
Console.WriteLine($"Name='{root.CurrentName}'");

Console.Write("5. CreatePropertyCondition ClassName=Edit... ");
IUIAutomationCondition cond = automation.CreatePropertyCondition(30012, "Edit");
Console.WriteLine($"OK");

Console.Write("6. FindFirst on root... ");
IUIAutomationElement child = root.FindFirst(
    Interop.UIAutomationClient.TreeScope.TreeScope_Descendants, cond);
Console.WriteLine($"OK child={child?.GetType().Name ?? "null"}");

if (child != null)
{
    Console.WriteLine($"   ClassName='{child.CurrentClassName}'");
    Console.WriteLine($"   Name='{child.CurrentName}'");
    Console.WriteLine($"   ControlType={child.CurrentControlType}");
}

Console.WriteLine("\n=== ALL TESTS PASSED ===");

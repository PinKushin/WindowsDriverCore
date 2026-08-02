# 005 — Pattern Access via COM

## Date: 2026-08-02

## UIA Patterns
Patterns are how UIA exposes element behavior (click, text, selection, etc.). Each pattern is a COM interface.

### Patterns We Use
| Pattern | Interface | Methods | Used For |
|---------|-----------|---------|----------|
| InvokePattern | `IUIAutomationInvokePattern` | `Invoke()` | Click |
| ValuePattern | `IUIAutomationValuePattern` | `SetValue(string)`, `get_Value(out string)` | SendKeys, GetText, Clear |
| SelectionItemPattern | `IUIAutomationSelectionItemPattern` | `Select()`, `get_IsSelected(out bool)` | Select, GetSelected |
| ExpandCollapsePattern | `IUIAutomationExpandCollapsePattern` | `Expand()`, `Collapse()` | Click fallback for dropdowns |

## How to Get a Pattern
```csharp
// Via raw COM — we define our own pattern interfaces
int hr = element.GetCurrentPatternAs(patternId, ref riid, out IntPtr patternPtr);
if (hr == 0 && patternPtr != IntPtr.Zero)
{
    var pattern = (IUIAutomationValuePattern)Marshal.GetObjectForIUnknown(patternPtr);
    pattern.SetValue("text");
    Marshal.ReleaseComObject(pattern);
}
```

## Pattern Interface Definitions
Located in `Automation/Com/Patterns.cs`:
- Each pattern interface has `QueryInterface`, `AddRef`, `Release` (IUnknown)
- Then the pattern-specific methods in vtable order
- `InterfaceType(ComInterfaceType.InterfaceIsIUnknown)` — COM uses IUnknown-based vtable

## Fallback Order for Click
1. Try `InvokePattern.Invoke()` — buttons, links
2. Try `ExpandCollapsePattern.Expand()` — dropdowns, menus
3. Fail with "element not clickable"

## Gotcha: Pattern Lifetime
- Pattern objects are COM interfaces — must be released with `Marshal.ReleaseComObject`
- Don't cache patterns long-term — the element they came from may become stale
- Get pattern, use it, release it. Same as element access.

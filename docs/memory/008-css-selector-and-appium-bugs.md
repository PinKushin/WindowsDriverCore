# 008 — CSS Selector Mapping and Appium Client Bugs

## Date: 2026-08-02

## The Old Appium Client Bug
`Microsoft.WinAppDriver.Appium.WebDriver.1.0.1-Preview` has a known bug: `FindElementByClassName("Edit")` sends `"css selector"` as the strategy instead of `"class name"`. The value is prefixed with `.` (e.g., `".Edit"`).

WinAppDriver handled this by treating `css selector` with `.`-prefixed values as class name searches.

## The Fix
In `ElementFinder.CreateCondition()`:
```csharp
"css selector" when value.StartsWith(".") =>
    ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_ClassNamePropertyId, value.Substring(1)),
"css selector" =>
    throw new WebDriverException(ErrorType.InvalidArgument,
        "Unexpected error. Unimplemented Command: css selector locator strategy is not supported"),
```

## All Locator Strategy Mappings
| Strategy | UIA Property/Action |
|----------|-------------------|
| `"accessibility id"` | `UIA_AutomationIdPropertyId` |
| `"class name"` | `UIA_ClassNamePropertyId` |
| `"name"` | `UIA_NamePropertyId` |
| `"id"` | RuntimeId (comma-separated int array) |
| `"tag name"` | `UIA_ControlTypePropertyId` (mapped from tag name) |
| `"css selector"` with `.` prefix | `UIA_ClassNamePropertyId` (strip `.`) |
| `"css selector"` without prefix | Not supported (throws) |
| `"xpath"` | Throws (UIA XPath is different from web XPath) |
| `"link text"` | Not supported |
| `"partial link text"` | Not supported |

## XPath Error Format
When an invalid XPath expression is sent, WinAppDriver returns:
```
Invalid XPath expression: {expr} (XPathLookupError)
```
Our server matches this format exactly.

## Files Changed
- `Automation/ElementFinder.cs` — `CreateCondition()` method

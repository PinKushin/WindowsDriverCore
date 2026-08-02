# Plan: Replace System.Windows.Automation with Raw IUIAutomation COM

## Context

WindowsDriverCore currently uses `System.Windows.Automation` — a managed wrapper around the `IUIAutomation` COM API. This is the same layer WinAppDriver uses, and it has the same bugs (#857 orphaned elements, #1079 random empty FindElements). Cheat tools bypass this wrapper entirely and talk to `IUIAutomation` COM directly — that's why they never have these problems.

The goal: replace `System.Windows.Automation` with raw COM interop so we have full control over every UIA call, no managed wrapper quirks, no hidden behavior.

## Current Touchpoints (what uses System.Windows.Automation)

| File | Usage |
|------|-------|
| `ElementFinder.cs` | `AutomationElement.FromHandle`, `FindFirst`, `FindAll`, `PropertyCondition`, `AndCondition`, `ControlType` |
| `ElementInteractor.cs` | `AutomationElement`, `InvokePattern`, `SelectionItemPattern`, `ExpandCollapsePattern`, `ValuePattern`, `ElementNotAvailableException` |
| `ElementStore.cs` | Stores `AutomationElement` objects, uses `GetRuntimeId()` |
| `ElementRoutes.cs` | `AutomationElement.FromHandle`, `PropertyCondition`, `FindFirst`, `FindAll`, `Condition.TrueCondition`, `BuildSourceXml`, screenshot via `BoundingRectangle` |

**Clean boundaries**: `IElementFinder` and `IElementInteractor` interfaces are COM-agnostic. Routes only touch `ElementStore` directly for screenshot/active-element/source.

## Architecture

```
Automation/
├── Com/
│   ├── IUIAutomation.cs           # Raw COM interface (vtable-aligned)
│   ├── IUIAutomationElement.cs    # Raw COM interface
│   ├── IUIAutomationCondition.cs  # Raw COM interface (base)
│   ├── IUIAutomationElementArray.cs
│   ├── IUIAutomationInvokePattern.cs
│   ├── IUIAutomationValuePattern.cs
│   ├── IUIAutomationSelectionItemPattern.cs
│   ├── IUIAutomationExpandCollapsePattern.cs
│   ├── UIAutomationFactory.cs     # Creates CUIAutomation COM object
│   └── ComConstants.cs            # Property IDs, ControlType IDs, Pattern IDs
├── Raw/
│   ├── RawAutomationElement.cs    # Managed wrapper around IUIAutomationElement COM pointer
│   ├── RawCondition.cs            # Managed wrapper around IUIAutomationCondition
│   └── RawPatternFactory.cs       # Gets pattern objects from elements
├── IElementFinder.cs              # UNCHANGED
├── IElementInteractor.cs          # UNCHANGED
├── ElementFinder.cs               # REWRITTEN to use Raw.*
├── ElementInteractor.cs           # REWRITTEN to use Raw.*
└── ElementStore.cs                # UPDATED to store RawAutomationElement
```

## Step 1: COM Interface Definitions

Define the raw COM interfaces in `Automation/Com/`. These must match the exact vtable layout of the real COM interfaces from `UIAutomationClient.dll`.

Key interfaces needed:
- `IUIAutomation` — factory: `ElementFromHandle`, `CreatePropertyCondition`, `CreateAndCondition`, `CreateTrueCondition`, `FindFirst`, `FindAll`
- `IUIAutomationElement` — element: properties (`Current.Name`, `Current.AutomationId`, etc.), `FindFirst`, `FindAll`, `GetCurrentPattern`
- `IUIAutomationCondition` — base condition (empty, just IUnknown)
- `IUIAutomationElementArray` — result collection: `Count`, `GetElement`
- Pattern interfaces: `IUIAutomationInvokePattern`, `IUIAutomationValuePattern`, `IUIAutomationSelectionItemPattern`, `IUIAutomationExpandCollapsePattern`

Plus `ComConstants.cs` with all the UIA property/control-type/pattern IDs as ints.

## Step 2: Managed Wrappers

- `RawAutomationElement` — wraps `IUIAutomationElement` COM pointer, provides clean C# API (`FindFirst`, `FindAll`, `GetName`, `GetAutomationId`, `GetBoundingRectangle`, etc.), handles stale-element detection via `COMException`
- `RawCondition` — wraps `IUIAutomationCondition` COM pointer
- `RawPatternFactory` — calls `element.GetCurrentPattern(patternId)` and returns typed pattern wrappers

## Step 3: Rewrite ElementFinder

Replace `System.Windows.Automation` calls with raw COM:
- `AutomationElement.FromHandle(hwnd)` → `factory.ElementFromHandle(hwnd)` → returns `RawAutomationElement`
- `new PropertyCondition(property, value)` → `factory.CreatePropertyCondition(propertyId, value)` → returns `RawCondition`
- `root.FindFirst(TreeScope.Descendants, condition)` → `rawElement.FindFirst(scope, condition)` → returns `RawAutomationElement`
- Stale element detection: catch `COMException` (RPC_E_SERVER_DIED) instead of `ElementNotAvailableException`

## Step 4: Rewrite ElementInteractor

Replace pattern access:
- `element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)` → `rawElement.TryGetCurrentPattern<IUIAutomationInvokePattern>(invokePatternId, out pattern)`
- Same for ValuePattern, SelectionItemPattern, ExpandCollapsePattern
- Property access: `element.Current.Name` → `rawElement.GetName()`, etc.

## Step 5: Update ElementStore

Change from `ConcurrentDictionary<string, AutomationElement>` to `ConcurrentDictionary<string, RawAutomationElement>`.

## Step 6: Update ElementRoutes

- `BuildSourceXml`: use `RawAutomationElement.FindChildren` + property accessors
- Active element: use `RawAutomationElement.FindFirst` with `HasKeyboardFocusProperty`
- Screenshot: use `RawAutomationElement.GetBoundingRectangle()`

## Step 7: Remove System.Windows.Automation

Remove `using System.Windows.Automation;` from all files. The `Microsoft.WindowsDesktop.App` framework reference can stay (it provides the COM type library for interop if needed).

## Step 8: Update CLAUDE.md

Remove the incorrect IUIAutomation COM reference (the current CLAUDE.md describes raw COM but the code uses managed wrapper). Replace with accurate documentation of the raw COM implementation.

## Risk: COM Vtable Alignment

The biggest risk is getting the vtable order wrong. The COM interfaces must be defined with methods in the exact order they appear in the IDL. Mitigation:
- Cross-reference with `UIAutomation.h` from Windows SDK
- Test each interface individually against a running app
- Start with `IUIAutomation.ElementFromHandle` (simplest call) and build outward

## Verification

1. `dotnet build` — 0 errors
2. `dotnet test WindowsDriverCore.Tests/` — all 7 integration tests pass
3. Manual test: launch TestApp, find Edit control by class name, send keys, get text
4. Compare element find results between old and new implementations on the same window

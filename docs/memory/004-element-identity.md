# 004 — Element Identity: RuntimeId

## Date: 2026-08-02

## How Elements Are Identified
- UIA RuntimeId (comma-separated int array, e.g. "12345,1,0") serves as the element ID
- Same approach as WinAppDriver — clients expect this format
- Stored in `ElementStore` as `ConcurrentDictionary<string, RawAutomationElement>`

## Why RuntimeId
- Stable for the lifetime of the element in the UIA tree
- Unique within a session (usually globally too)
- Defined by the UIA framework, not by us — no collision risk from our implementation
- Clients use it as an opaque string — they don't parse it

## Stale Detection
- Before searching under a parent, check `parent.GetBoundingRectangle()` — returns `RectD.Empty` if stale
- Catch `COMException` during any element access — element may have been removed from tree
- `IsAlive()` method on `RawAutomationElement` — reads a property, catches COM exceptions

## Element Lifecycle
1. `ElementFinder.FindElement` → searches tree → gets `RawAutomationElement` → stores in `ElementStore` → returns ID string
2. `ElementRoutes` receives ID → retrieves from `ElementStore` → calls `RawAutomationElement` methods
3. Element becomes stale when the UI removes it (window close, control rebuild, etc.)
4. Stale detection catches this and returns appropriate WebDriver error

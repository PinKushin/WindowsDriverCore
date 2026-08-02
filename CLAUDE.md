# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

WindowsDriverCore is an open-source replacement for WinAppDriver. It implements the WebDriver/JSON Wire Protocol that Appium clients expect, listening on `http://127.0.0.1:4723`.

**Current state**: Uses `System.Windows.Automation` (managed wrapper around `IUIAutomation` COM). Planned migration to raw `IUIAutomation` COM interop for direct control — see `docs/plan-raw-com-migration.md`.

## Known WinAppDriver issues this solves

- [WinAppDriver #857](https://github.com/microsoft/WinAppDriver/issues/857): Elements exist on screen but aren't in the UIA tree that `FindElement` searches. ListView items get "orphaned" — present in the visual tree but with no parent in the UIA hierarchy.
- [WinAppDriver #1079](https://github.com/microsoft/WinAppDriver/issues/1079): `FindElements` randomly returns empty results.
- WinAppDriver is closed-source (archived June 2025) so these bugs cannot be fixed externally.

## Architecture

ASP.NET Core Minimal API on Kestrel. All services are singletons.

```
Program.cs                     → DI + middleware + route registration
Automation/
  IElementFinder.cs            → FindElement, FindElements, FindElementInElement
  ElementFinder.cs             → UIAutomation-based implementation (TO BE REWRITTEN)
  IElementInteractor.cs        → Click, SendKeys, GetText, GetAttribute, etc.
  ElementInteractor.cs         → UIAutomation pattern-based implementation (TO BE REWRITTEN)
  ElementStore.cs              → ConcurrentDictionary<Guid, AutomationElement> cache
Windows/
  Win32.cs                     → P/Invoke: EnumWindows, GetWindowText, SendInput, etc.
  WindowFinder.cs              → Multi-strategy window discovery (Win32 + UWP)
Applications/
  AppLauncher.cs               → Process.Start + UWP COM activation (IApplicationActivationManager)
  SessionCleanupService.cs     → BackgroundService polling for orphaned sessions
Sessions/
  SessionStore.cs              → ConcurrentDictionary-backed session storage
Routes/
  StatusRoutes.cs              → GET /status
  SessionRoutes.cs             → POST/DELETE /session, window management
  ElementRoutes.cs             → Element CRUD, click, text, attributes, screenshots
```

## Key design decisions

- **Element identity**: UIA RuntimeId (comma-separated int array) serves as element ID — same approach as WinAppDriver
- **Stale element detection**: Checks `parent.Current.BoundingRectangle` before search; catches `ElementNotAvailableException` and `COMException`
- **Window finding**: Win32 path first (EnumWindows + PID filter), then UWP path (ApplicationFrameWindow + CoreWindow), with new-window detection for slow-starting apps
- **UWP launching**: `IApplicationActivationManager` COM interface, not shell: protocol
- **Process kill**: 5-step cascade: WM_CLOSE → taskkill /F /T → bottom-up child kill → Process.Kill(entireProcessTree). Explorer special-cased.
- **DPI awareness**: `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` at startup

## Commands

```powershell
# Build
dotnet build WindowsDriverCore.slnx

# Run tests (requires server running at http://127.0.0.1:4723)
dotnet test WindowsDriverCore.Tests/WindowsDriverCore.Tests.csproj

# Run server
dotnet run --project WindowsDriverCore/WindowsDriverCore.csproj
```

## IUIAutomation COM reference (for raw COM migration)

### Primary entry point

```csharp
IUIAutomation automation = new CUIAutomation();
```

### Key COM interfaces

| Interface | GUID | Purpose |
|-----------|------|---------|
| `IUIAutomation` | `14314595-B0AD-4A2C-B385-AC53C31A1D25` | Root factory |
| `IUIAutomationElement` | `D827F2C0-3771-4AD9-872E-F0246972138F` | Element properties + tree navigation |
| `IUIAutomationCondition` | `352FFBA8-0973-437C-A6E3-20FA2465F1AC` | Base condition (empty interface) |
| `IUIAutomationInvokePattern` | `FB377FBE-8EA6-46D5-9C73-6499642D3059` | Click/activate |
| `IUIAutomationValuePattern` | `A9468346-2255-4FF4-A07C-75353AE7E3E5` | Get/set text values |
| `IUIAutomationSelectionItemPattern` | `A8EFA66A-0FDA-421A-9194-38021F3578EA` | Select items |
| `IUIAutomationExpandCollapsePattern` | `619B0F0D-0936-427C-8936-843FEE4CCFB0` | Expand/collapse |

### Property IDs (from `UIAutomationClient.h`)

```
UIA_ControlTypePropertyId      = 30003
UIA_NamePropertyId             = 30005
UIA_AutomationIdPropertyId     = 30011
UIA_ClassNamePropertyId        = 30012
UIA_HasKeyboardFocusPropertyId = 30026
UIA_IsEnabledPropertyId        = 30010
UIA_NativeWindowHandlePropertyId = 30020
UIA_BoundingRectanglePropertyId = 30001
UIA_ProcessIdPropertyId        = 30002
UIA_RuntimeIdPropertyId        = 30007
```

### Pattern IDs

```
UIA_InvokePatternId     = 10000
UIA_ValuePatternId      = 10002
UIA_SelectionItemPatternId = 10010
UIA_ExpandCollapsePatternId = 10005
```

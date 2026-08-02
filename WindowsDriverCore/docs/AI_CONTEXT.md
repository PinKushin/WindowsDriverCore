# WindowsDriverCore — AI Context

## Project Metadata
- **Name**: WindowsDriverCore
- **Description**: A modern, reliable, open-source Windows automation driver built for Appium and .NET 10.
- **TargetFramework**: net10.0-windows
- **MainProject**: WindowsDriverCore.csproj
- **Port**: 4723 (http://127.0.0.1:4723)
- **launchBrowser**: false
- **Licenses**: MIT
- **README**: docs/README.md

## Current State: Raw COM Migration (In Progress)

**Migrating from `System.Windows.Automation` to raw `IUIAutomation` COM interop.**

The managed wrapper adds hidden behavior and is the source of WinAppDriver bugs #857 and #1079. We're replacing it with direct COM pointer manipulation for full control.

### Migration Status
- ✅ Raw COM layer built: `Automation/Com/` — `IUIAutomation.cs`, `IUIAutomationElement.cs`, `IUIAutomationCondition.cs`, `Patterns.cs`, `ComConstants.cs`, `UIAutomationFactory.cs`
- ✅ Managed wrappers: `Automation/Raw/` — `RawAutomationElement.cs`, `RawCondition.cs`, `ConditionFactory.cs`
- ✅ All COM interfaces use `IntPtr` for out params (no managed COM marshaling failures)
- 🔄 `ElementFinder.cs` — rewritten with raw COM
- 🔄 `ElementInteractor.cs` — rewritten with raw COM
- 🔄 `ElementStore.cs` — stores `RawAutomationElement` instead of `AutomationElement`
- 🔄 `ElementRoutes.cs` — updated to use raw COM
- ❌ Build status: needs verification after IntPtr migration
- ❌ Tests: 8/8 Win32 smoke tests were passing before IntPtr migration, need to get back to 8/8 ASAP

### Key Design Decision: IntPtr Everywhere
If `hwnd` needs `IntPtr`, then ALL COM out parameters returning interface pointers need `IntPtr` too. The .NET COM marshaler fails with `InvalidCastException` when it tries to wrap returned pointers in custom managed interfaces. See `docs/memory/001-intptr-com-migration.md`.

### Approach: Cheat-Tool-Level Control
Not copying WinAppDriver's approach. Raw COM, no hidden behavior, `unsafe` where needed for performance. See `docs/memory/002-cheat-tool-approach.md`.

## Architecture (SOLID Principles)

### Project Structure
```
WindowsDriverCore/
├── Program.cs                     # Entry point: DI registration + route mapping + exception middleware
├── Messages/                      # WebDriver protocol DTOs (records)
│   ├── WebDriverResponse.cs       # { value: T } wrapper for all endpoints
│   ├── ErrorResponse.cs           # { value: { error, message, stacktrace } }
│   ├── StatusInfo.cs              # BuildInfo + OsInfo (for /status endpoint)
│   ├── SessionRequest.cs          # POST /session request body
│   ├── SessionInfo.cs             # sessionId + capabilities response
│   ├── ElementRequest.cs          # { using, value } for element lookup
│   └── ElementInfo.cs             # { elementId }
├── Sessions/                      # Session lifecycle
│   ├── ISessionStore.cs           # Interface + SessionContext record
│   └── SessionStore.cs            # ConcurrentDictionary-backed implementation
├── Windows/                       # Win32 window attachment
│   ├── IWindowFinder.cs           # Interface for HWND discovery
│   └── WindowFinder.cs            # EnumWindows interop implementation
├── Applications/                  # Process launching
│   ├── IAppLauncher.cs            # Interface for app lifecycle
│   └── AppLauncher.cs             # Process.Start + Kill implementation
├── Automation/                    # UIAutomation element interaction (RAW COM)
│   ├── Com/                       # Raw COM interface definitions
│   │   ├── IUIAutomation.cs       # Root factory — all out params use IntPtr
│   │   ├── IUIAutomationElement.cs # Element properties + tree — IntPtr out params
│   │   ├── IUIAutomationCondition.cs # Condition + ElementArray + CacheRequest
│   │   ├── Patterns.cs            # InvokePattern, ValuePattern, SelectionItem, ExpandCollapse, TreeWalker
│   │   ├── ComConstants.cs        # UIA property/pattern/control type IDs
│   │   └── UIAutomationFactory.cs # CoCreateInstance wrapper
│   ├── Raw/                       # Managed wrappers over COM pointers
│   │   ├── RawAutomationElement.cs # IntPtr + marshaling for method calls
│   │   ├── RawCondition.cs        # Stores IntPtr condition pointer
│   │   └── ConditionFactory.cs    # Creates conditions from COM factory
│   ├── IElementFinder.cs          # Interface for element search
│   ├── ElementFinder.cs           # Raw COM FindFirst/FindAll
│   ├── IElementInteractor.cs      # Interface for element actions
│   ├── ElementInteractor.cs       # Click, SendKeys, GetText via raw COM patterns
│   └── ElementStore.cs            # ConcurrentDictionary<Guid, RawAutomationElement> cache
├── Screenshots/                   # Screenshot capture
│   └── IScreenshotCapture.cs      # Interface for GDI-based capture
├── Routes/                        # Minimal API route registration (extension methods)
│   ├── StatusRoutes.cs            # GET /status/
│   ├── SessionRoutes.cs           # POST /session, DELETE /session/{id}, GET /sessions, /title, /window/*
│   ├── ElementRoutes.cs           # POST .../element, .../click, .../value, GET .../text, .../enabled, etc.
│   └── ScreenshotRoutes.cs        # (planned) GET .../screenshot
└── ErrorHandling/                 # Exception types + error code mapping
    ├── WebDriverException.cs      # Typed exception with error code + HTTP status
    └── ErrorType.cs               # Constants for WebDriver error strings
```

### SOLID Application
- **S** — Each class has one responsibility (SessionStore manages only sessions, WindowFinder finds only windows, ElementStore manages element lifecycle)
- **O** — New locator strategies are added as new implementations — no existing code changes needed
- **L** — All services accessed through interfaces (ISessionStore, IWindowFinder, etc.) — any impl is substitutable
- **I** — IElementFinder (search) vs IElementInteractor (actions) — not one monolithic automation service
- **D** — Routes inject interfaces; Program.cs wires concrete types via `builder.Services`

### DI Registration (Program.cs)
```
ISessionStore      → SessionStore (singleton — stateful)
IWindowFinder      → WindowFinder (singleton)
IAppLauncher       → AppLauncher (singleton)
ElementStore       → ElementStore (singleton — shared element cache)
IElementFinder     → ElementFinder (singleton)
IElementInteractor → ElementInteractor (singleton)
IScreenshotCapture → (planned)
```

## Core WebDriver Endpoints

### Protocol Notes
- `/status/` returns build+os info **at the top level** (no `{ value: ... }` wrapper)
- All other endpoints wrap responses in `{ value: ... }`
- Session and element routes use sessionId/elementId from URL params to resolve their lifetime
- JSON serialization uses **camelCase** (configured in Program.cs via `ConfigureHttpJsonOptions`)
- Unhandled exceptions are caught by middleware and returned as WebDriver error responses
- Element responses use W3C format: `{"value": {"element-6066-...": "id", "ELEMENT": "id"}}`

### Implemented Endpoints
1. `GET /status/` — Build + OS info from assembly metadata + runtime
2. `POST /session` — Launch app, poll for window, create session
3. `DELETE /session/{sessionId}` — Close process, remove session
4. `GET /sessions` — List all active sessions
5. `GET /session/{sessionId}/title` — Window title via Win32 GetWindowText
6. `GET /session/{sessionId}/window/handle` — Current window handle (hex)
7. `GET /session/{sessionId}/window/handles` — Child window handles (hex)
8. `POST /session/{sessionId}/element` — Find element (accessibility id, class name, name, id, tag name)
9. `POST /session/{sessionId}/elements` — Find multiple elements
10. `POST /session/{sessionId}/element/{elementId}/element` — Find child element
11. `POST /session/{sessionId}/element/{elementId}/elements` — Find multiple child elements
12. `POST /session/{sessionId}/element/{elementId}/click` — Click element (InvokePattern / ExpandCollapsePattern)
13. `POST /session/{sessionId}/element/{elementId}/value` — Send keys (ValuePattern)
14. `POST /session/{sessionId}/element/{elementId}/clear` — Clear element (ValuePattern)
15. `GET /session/{sessionId}/element/{elementId}/text` — Get text (ValuePattern → Name fallback)
16. `GET /session/{sessionId}/element/{elementId}/enabled` — IsEnabled check
17. `GET /session/{sessionId}/element/{elementId}/displayed` — Bounding rectangle check
18. `GET /session/{sessionId}/element/{elementId}/name` — ControlType.ProgrammaticName
19. `GET /session/{sessionId}/element/{elementId}/attribute/{name}` — Various attributes (name, automationid, classname, etc.)
20. `GET /session/{sessionId}/element/{elementId}/selected` — SelectionItemPattern
21. `GET /session/{sessionId}/element/{elementId}/location` — Bounding rectangle X,Y
22. `GET /session/{sessionId}/element/{elementId}/size` — Bounding rectangle width,height
23. `GET /session/{sessionId}/element/{elementId}/screenshot` — GDI BitBlt capture (needs testing)
24. `GET /session/{sessionId}/source` — UIA tree as XML
25. `POST /session/{sessionId}/back` — Alt+Left keyboard shortcut (needs testing)
26. `POST /session/{sessionId}/forward` — Alt+Right keyboard shortcut (needs testing)
27. `GET /session/{sessionId}/element/{elementId}/location_in_view` — Visible location (delegates to location)

### Planned Endpoints (Need Review & Testing)
- Window management: position, size, maximize — partially implemented in SessionRoutes, needs validation
- Keyboard: `POST /session/{sessionId}/keys` — global key injection, not yet implemented
- Appium: `POST /session/{sessionId}/appium/app/launch`, `/close` — not yet implemented

## Implementation Details

### Raw COM Layer (Automation/Com/)
- All COM interfaces use `[PreserveSig]` and return `int` HRESULT
- All out params returning interface pointers use `IntPtr` — manual marshaling via `Marshal.GetObjectForIUnknown`
- `UIAutomationFactory.Create()` uses `CoCreateInstance` to create `CUIAutomation`
- Pattern interfaces: `IUIAutomationInvokePattern`, `IUIAutomationValuePattern`, `IUIAutomationSelectionItemPattern`, `IUIAutomationExpandCollapsePattern`
- `IUIAutomationTreeWalker` for tree navigation (all IntPtr params)

### Raw Wrappers (Automation/Raw/)
- `RawAutomationElement` — stores `IntPtr _rawPtr` + `IUIAutomationElement _element` for method calls
- `RawCondition` — stores `IntPtr _conditionPtr` (opaque, never call methods on conditions)
- `ConditionFactory` — static factory, initialized once with `IUIAutomation` instance
- Stale detection: `IsAlive()` reads BoundingRectangle, catches COMException

### AppLauncher (Applications/AppLauncher.cs)
- Uses `System.Diagnostics.Process.Start()` with `UseShellExecute = true`
- Tracks processes in `Dictionary<int, Process>`
- `Close()` kills via `Kill(entireProcessTree: true)` with try/catch for already-exited

### WindowFinder (Windows/WindowFinder.cs)
- Win32 interop: `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindowText`
- Filters by process ID + visibility, skips owned windows (`GetWindow(GW_OWNER)`)
- Prefers windows with text titles, falls back to first visible top-level window

### ElementStore (Automation/ElementStore.cs)
- Maps element IDs (comma-separated RuntimeId) to `RawAutomationElement` objects
- Thread-safe via `ConcurrentDictionary`
- Shared across all sessions (elements from any session can be accessed)

### ElementFinder (Automation/ElementFinder.cs)
- Uses `IUIAutomation.ElementFromHandle(hwnd)` to get root element (raw COM)
- Supports strategies: accessibility id, class name, name, id, tag name, css selector
- Sub-element search: find within a parent element by element ID
- Maps tag names to UIAutomation ControlType IDs

### ElementInteractor (Automation/ElementInteractor.cs)
- Click: tries InvokePattern → SelectionItemPattern → ExpandCollapsePattern fallback
- SendKeys: ValuePattern.SetValue
- GetText: ValuePattern.Value → Name property fallback
- GetEnabled: IsEnabled property
- GetDisplayed: BoundingRectangle check (non-empty, non-zero size)
- GetTagName: ControlType.ProgrammaticName
- GetAttribute: Maps attribute names to element properties
- Clear: ValuePattern.SetValue("")
- GetSelected: SelectionItemPattern.IsSelected
- GetCoordinates/GetSize: BoundingRectangle

### Exception Middleware (Program.cs)
- Catches `WebDriverException` → returns typed error with correct HTTP status
- Catches all other exceptions → returns 500 with `unknown error`
- Response format: `{"value": {"error": "...", "message": "...", "stacktrace": ""}}`

## Test Suite Strategy

### WinAppDriver Test Suite (Compatibility Contract)
- **418 tests** across 4 projects (AbsoluteXPath, Input, UWPControls, WebDriverAPI)
- Primary goal: pass ALL of these — this is the compatibility guarantee
- Run against our server at `http://127.0.0.1:4723`
- Separate CI workflow for visibility
- **CommonTestSettings.cs**: `WindowsApplicationDriverUrl = http://127.0.0.1:4723`
- **Test framework**: MSTest (net48)

### Custom Win32 Test Suite (Our Tests)
- **8 smoke tests** — were 8/8 before IntPtr migration, need to get back to 8/8 ASAP
- Tests: CreateSession, FindElement, SendKeys, GetAttribute, Window_Size, Window_Position, Window_Maximize_Restore, DeleteSession
- Separate CI workflow
- Expand with edge cases after baseline is restored

### Unit & Integration Tests (Needed)
- Currently very few unit or integration tests
- Target: 100% coverage on core logic (ElementFinder, ElementInteractor, ElementStore, ConditionFactory)
- Fix any failing tests — this should make UI tests just work
- Not many needed, but coverage should be complete

### Application Under Tests
| App | AUMID | Used By |
|-----|-------|---------|
| TestApp (Win32) | N/A | Custom smoke tests — Windows 11 lacks good native Win32 test target |
| AppUIBasics | `WinAppDriver.AppUIBasics_xh1ske9axcpv8!App` | UWPControls tests |
| Input | (UWP app) | Input tests |
| Xaml-Controls-Gallery | (UWP app) | Additional UI tests |

**Note**: AppUIBasics must be built and deployed before running UWPControls tests.

## Development Workflow
1. Implement endpoint in `Routes/*Routes.cs` using interface injection
2. Add business logic in `Sessions/*`, `Windows/*`, `Automation/*`, etc.
3. `dotnet build` — 0 errors, 0 warnings
4. `dotnet run` — verify with curl/Invoke-WebRequest
5. Run the relevant WinAppDriver test to validate
6. Commit small, focused changes

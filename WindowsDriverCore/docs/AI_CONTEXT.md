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
├── Automation/                    # UIAutomation element interaction
│   ├── IElementFinder.cs          # Interface for element search
│   ├── ElementFinder.cs           # UIAutomation FindFirst/FindAll + XPath
│   ├── IElementInteractor.cs      # Interface for element actions
│   ├── ElementInteractor.cs       # Click, SendKeys, GetText, etc. via UIAutomation patterns
│   └── ElementStore.cs            # In-memory mapping of element IDs → AutomationElement
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
8. `POST /session/{sessionId}/element` — Find element (accessibility id, class name, name, id, tag name, xpath)
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

### Planned Endpoints
- `GET /session/{sessionId}/screenshot` — GDI BitBlt capture
- `POST /session/{sessionId}/element/{elementId}/location_in_view` — Visible location
- Window management: position, size, maximize
- Keyboard: `POST /session/{sessionId}/keys`
- Navigation: `POST /session/{sessionId}/back`, `/forward`
- Appium: `POST /session/{sessionId}/appium/app/launch`, `/close`

## Implementation Details

### AppLauncher (Applications/AppLauncher.cs)
- Uses `System.Diagnostics.Process.Start()` with `UseShellExecute = true`
- Tracks processes in `Dictionary<int, Process>`
- `Close()` kills via `Kill(entireProcessTree: true)` with try/catch for already-exited

### WindowFinder (Windows/WindowFinder.cs)
- Win32 interop: `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindowText`
- Filters by process ID + visibility, skips owned windows (`GetWindow(GW_OWNER)`)
- Prefers windows with text titles, falls back to first visible top-level window

### ElementStore (Automation/ElementStore.cs)
- Maps element IDs (comma-separated RuntimeId) to `AutomationElement` objects
- Thread-safe via `ConcurrentDictionary`
- Shared across all sessions (elements from any session can be accessed)

### ElementFinder (Automation/ElementFinder.cs)
- Uses `AutomationElement.FromHandle(windowHandle)` to get root element
- Supports strategies: accessibility id, class name, name, id, tag name, xpath
- XPath support: parses `//ControlType[@Attribute="Value"]` patterns
- Sub-element search: find within a parent element by element ID
- Maps tag names to UIAutomation ControlType objects

### ElementInteractor (Automation/ElementInteractor.cs)
- Click: tries InvokePattern → ExpandCollapsePattern fallback
- SendKeys: ValuePattern.SetValue
- GetText: ValuePattern.Value → Name property fallback
- GetEnabled: IsEnabled property
- GetDisplayed: BoundingRectangle check (non-empty, non-zero size)
- GetTagName: ControlType.ProgrammaticName
- GetAttribute: Maps attribute names to AutomationElement.Current properties
- Clear: ValuePattern.SetValue("") 
- GetSelected: SelectionItemPattern.IsSelected
- GetCoordinates/GetSize: BoundingRectangle

### Exception Middleware (Program.cs)
- Catches `WebDriverException` → returns typed error with correct HTTP status
- Catches all other exceptions → returns 500 with `unknown error`
- Response format: `{"value": {"error": "...", "message": "...", "stacktrace": ""}}`

## Test Suite Integration
- **Reference**: ../WinAppDriver/Tests/WebDriverAPI/WebDriverAPI.csproj (net48)
- **4 test projects**: AbsoluteXPath, Input, UWPControls, WebDriverAPI (418 total tests)
- **CommonTestSettings.cs**: `WindowsApplicationDriverUrl = http://127.0.0.1:4723`
- **App IDs**: CalculatorAppId (`Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`), DesktopAppId (`Root`), ExplorerAppId (`C:\Windows\System32\explorer.exe`), NotepadAppId (`C:\Windows\System32\notepad.exe`)
- **Test framework**: MSTest (net48)
- **Package Management**: Tests use packages.config with pre-restored packages
- **Current passing tests**: 1 (`Status.GetStatus`)
- **Remaining blockers for more tests**: UWP app launching (AUMID), desktop session ("Root"), /window/position, /window/size, /window/maximize, /keys, /appium/app/close, /appium/app/launch

### Application Under Tests
The WinAppDriver test suite references test apps in `C:\Users\pinku\source\repos\PinKushin\WinAppDriver\ApplicationUnderTests`:

| App | AUMID | Used By |
|-----|-------|---------|
| AppUIBasics | `WinAppDriver.AppUIBasics_xh1ske9axcpv8!App` | UWPControls tests (must be built and deployed first) |
| Input | (UWP app) | Input tests |
| Xaml-Controls-Gallery | (UWP app) | Additional UI tests |

**Note**: AppUIBasics must be built and deployed before running UWPControls tests:
1. Open `ApplicationUnderTests\AppUIBasics\AppUIBasics.sln` in Visual Studio
2. Select configuration (e.g. x86) and Run
3. App installs; can be closed after first launch

## Development Workflow
1. Implement endpoint in `Routes/*Routes.cs` using interface injection
2. Add business logic in `Sessions/*`, `Windows/*`, `Automation/*`, etc.
3. `dotnet build` — 0 errors, 0 warnings
4. `dotnet run` — verify with curl/Invoke-WebRequest
5. Run the relevant WinAppDriver test to validate
6. Commit small, focused changes

# WindowsDriverCore — AI Context

## Project Metadata
- **Name**: WindowsDriverCore
- **Description**: A modern, reliable, open-source Windows automation driver built for Appium and .NET 10.
- **TargetFramework**: net10.0
- **MainProject**: WindowsDriverCore.csproj
- **Port**: 4723 (http://127.0.0.1:4723)
- **launchBrowser**: false
- **Licenses**: MIT
- **README**: docs/README.md

## Architecture (SOLID Principles)

### Project Structure
```
WindowsDriverCore/
├── Program.cs                     # Entry point: DI registration + route mapping
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
│   └── IWindowFinder.cs           # Interface for HWND discovery
├── Applications/                  # Process launching
│   └── IAppLauncher.cs            # Interface for app lifecycle
├── Automation/                    # UIAutomation element interaction
│   ├── IElementFinder.cs          # Interface for element search
│   └── IElementInteractor.cs      # Interface for element actions
├── Screenshots/                   # Screenshot capture
│   └── IScreenshotCapture.cs      # Interface for GDI-based capture
├── Routes/                        # Minimal API route registration (extension methods)
│   ├── StatusRoutes.cs            # GET /status/
│   ├── SessionRoutes.cs           # POST /session, DELETE /session/{id}
│   ├── ElementRoutes.cs           # POST .../element, .../click, .../value
│   └── ScreenshotRoutes.cs        # GET .../screenshot
└── ErrorHandling/                 # Exception types + error code mapping
    ├── WebDriverException.cs      # Typed exception with error code + HTTP status
    └── ErrorType.cs               # Constants for WebDriver error strings
```

### SOLID Application
- **S** — Each class has one responsibility (SessionStore manages only sessions, WindowFinder finds only windows)
- **O** — New locator strategies are added as new implementations — no existing code changes needed
- **L** — All services accessed through interfaces (ISessionStore, IWindowFinder, etc.) — any impl is substitutable
- **I** — IElementFinder (search) vs IElementInteractor (actions) — not one monolithic automation service
- **D** — Routes inject interfaces; Program.cs wires concrete types via `builder.Services`

### DI Registration (Program.cs)
```
ISessionStore   → SessionStore (singleton — stateful)
IWindowFinder   → to be added
IAppLauncher    → to be added
IElementFinder  → to be added
IElementInteractor → to be added
IScreenshotCapture → to be added
```

## Core WebDriver Endpoints (WIP — matching WinAppDriver protocol)

### Protocol Notes
- `/status/` returns build+os info **at the top level** (no `{ value: ... }` wrapper)
- All other endpoints wrap responses in `{ value: ... }`
- Session and element routes use sessionId/elementId from URL params to resolve their lifetime

### Endpoints
1. `GET /status/`
   - Returns: `{"build": {"version": "...", "revision": "...", "time": "..."}, "os": {"arch": "...", "name": "windows", "version": "..."}}`

2. `POST /session`
   - Body: `{"capabilities": {"alwaysMatch": {"platformName": "Windows", "app": "..."}}}`
   - Returns: `{"value": {"sessionId": "uuid", "capabilities": {...}}}`

3. `DELETE /session/{sessionId}`
   - Returns: `{"value": null}`

4. `POST /session/{sessionId}/element`
   - Body: `{"using": "accessibility id", "value": "..."}`
   - Returns: `{"value": {"elementId": "uuid", "type": "element"}}`

5. `POST /session/{sessionId}/element/{elementId}/click`
   - Returns: `{"value": null}`

6. `POST /session/{sessionId}/element/{elementId}/value`
   - Body: `{"text": "hello world"}`
   - Returns: `{"value": null}`

7. `GET /session/{sessionId}/screenshot`
   - Returns: `{"value": "iVBORw0KGgoAAAANSUhEUg..."}`

## Test Suite Integration
- **Reference**: ../WinAppDriver/Tests/WebDriverAPI/WebDriverAPI.csproj (net48)
- **4 test projects**: AbsoluteXPath, Input, UWPControls, WebDriverAPI
- **CommonTestSettings.cs**: `WindowsApplicationDriverUrl = http://127.0.0.1:4723`
- **App IDs**: CalculatorAppId (`Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`), DesktopAppId (`Root`), etc.
- **Package Management**: Tests use packages.config with pre-restored packages under their own packages/ folders

## Development Workflow
1. Implement endpoint in `Routes/*Routes.cs` using interface injection
2. Add business logic in `Sessions/*`, `Windows/*`, `Automation/*`, etc.
3. `dotnet build` — 0 errors, 0 warnings
4. `dotnet run` — verify with curl/Invoke-WebRequest
5. Run the relevant WinAppDriver test to validate
6. Commit small, focused changes

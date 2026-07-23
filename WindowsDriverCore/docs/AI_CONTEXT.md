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

## Core WebDriver Endpoints (Initial Release)
The server implements these 7 core endpoints:

1. `GET /status`  
   - Returns: `{"value": {"ready": true, "message": "Windows WebDriver replacement is running"}}`

2. `POST /session`  
   - Body: `{"capabilities": {"alwaysMatch": {"platformName": "Windows", "app": "Path/To/App.exe"}}}`
   - Returns: `{"value": {"sessionId": "uuid", "capabilities": {...}}}`

3. `DELETE /session/{sessionId}`  
   - Returns: `{"value": null}`

4. `POST /session/{sessionId}/element`  
   - Body: `{"using": "accessibility id", "value": "LoginButton"}`  
   - Returns: `{"value": {"elementId": "uuid", "type": "element"}}`

5. `POST /session/{sessionId}/element/{elementId}/click`  
   - Returns: `{"value": null}`

6. `POST /session/{sessionId}/element/{elementId}/value`  
   - Body: `{"text": "hello world"}`
   - Returns: `{"value": null}`

7. `GET /session/{sessionId}/screenshot`  
   - Returns: `{"value": "iVBORw0KGgoAAAANSUhEUg..."}`

## Test Suite Integration (WinAppDriver)
WindowsDriverCore is intended to be replaced for WinAppDriver:

- **Reference**: ../WinAppDriver/Tests/WebDriverAPI/WebDriverAPI.csproj (net48)
- **Test Projects** (4 total):
  - `../WinAppDriver/Tests/AbsoluteXPath/AbsoluteXPath.csproj`
  - `../WinAppDriver/Tests/Input/Input.csproj`
  - `../WinAppDriver/Tests/UWPControls/UWPControls.csproj`
  - `../WinAppDriver/Tests/WebDriverAPI/WebDriverAPI.csproj`

- **CommonStartSettings.cs**: Contains all test app IDs and URL configuration (http://127.0.0.1:4723)
- **Package Management**: All tests restored with proper nuget packages under their `packages/` folders

## Architecture Overview
From `docs/technical-design.md`:

- **WebDriverServer**: Minimal API host (ASP.NET Core)
- **CommandDispatcher**: Routes WebDriver commands to handlers  
- **SessionManager**: Tracks active sessions  
- **AppLauncher**: Launches and attaches to Windows apps  
- **WindowFinder**: Win32‑based HWND discovery  
- **UIAutomationLayer**: Element search and interaction  
- **ScreenshotService**: High‑performance GDI capture  
- **WebDriverModel**: DTOs for protocol compliance  

Key techniques:
- **Win32**: EnumWindows, GetWindowThreadProcessId, IsWindowVisible  
- **UIAutomation**: from hwnd → AutomationElement → FindFirst/FindAll  
- **ScreenShots**: GDI BitBlt → PNG → Base64  

## Developer Workflow (From README.md)
1. Clone repo
2. **Build with .NET 10** (TargetFramework: net10.0)
3. Run WindowsDriverCore.Server  
4. Connect via Appium to http://127.0.0.1:4723  
5. Implement changes (endpoints in Minimal API, handlers in Commands/)
6. Test manually + unit tests from WinAppDriver test suite
7. Open .slnx, reference the 4 test projects
8. Commit small, focused changes

## Dependencies (from winappdriver‑Tests packages/)
- **WindowsDriverCore.Server**: (New, needs appium-dotnet-driver, Newtonsoft.Json)
- **TestProjects**: Selenium.WebDriver 3.8.0 / 3.11.2, appium-dotnet-driver, Castle.Core, Newtonsoft.Json

## Project Non‑Test Assets
All generated files in `.vs/` directories, `bin/`, `obj/` are artifacts. Only source code matters:

- Driver code in WindowsDriverCore/WindowsDriverCore.csproj
- Docs folder: `technical-design.md`, `implementation-guide.md`, `AI_CONTEXT.md`, `ai_context.json`, `README.md`
- Packages folder for test dependencies
- `.gitignore` includes all build artifacts

---
**Purpose**: Replace un‑maintained WinAppDriver with a modern, open‑source, community‑driven WebDriver server for Windows automation.

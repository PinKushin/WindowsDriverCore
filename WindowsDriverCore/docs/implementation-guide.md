# Implementation Guide

This guide explains how WindowsDriverCore is structured, how its major components work, and how to safely extend or modify the implementation. It is intended for contributors and maintainers.

---

## 1. Project Structure

A typical layout for WindowsDriverCore:

    src/
      WindowsDriverCore.Server/
        Program.cs
        Routing
        Minimal API setup
      WindowsDriverCore.Core/
        Sessions/
          SessionManager.cs
          Session.cs
        Commands/
          CommandDispatcher.cs
          Handlers/
        Automation/
          UIAutomationLayer.cs
          ElementLocator.cs
          ElementActions.cs
        Windows/
          WindowFinder.cs
          AppLauncher.cs
        Screenshots/
          ScreenshotService.cs
        Protocol/
          WebDriverModel.cs
          JsonResponseFactory.cs
    docs/
      technical-design.md
      implementation-guide.md
      roadmap.md

Each folder has a single responsibility. Keep it that way when adding new features.

---

## 2. WebDriver Server (Minimal API)

The WebDriver server is a lightweight ASP.NET Core Minimal API application listening on port 4723.

### Responsibilities

- Accept HTTP requests from Appium / WebDriver clients  
- Route requests to command handlers  
- Manage session lifecycle  
- Serialize responses according to the WebDriver protocol  

### Request Flow

1. HTTP request arrives at an endpoint such as:
       /session
       /session/{id}/element
       /session/{id}/screenshot
2. Minimal API parses route + JSON body.
3. CommandDispatcher receives:
       method
       route
       payload
4. Dispatcher selects the correct handler.
5. Handler uses SessionManager, UIAutomationLayer, WindowFinder, etc.
6. Response is serialized using WebDriverModel + JsonResponseFactory.

---

## 3. Session Management

SessionManager tracks active automation sessions.

### Session Contents

- SessionId  
- Target application path or process  
- Main window handle (HWND)  
- Root AutomationElement  
- Capabilities (timeouts, etc.)  

### Lifecycle

- CreateSession:
    - Parse capabilities
    - Launch or attach to app
    - Locate main window via WindowFinder
    - Initialize UIAutomationLayer
    - Store session

- GetSession:
    - Retrieve session by ID

- DeleteSession:
    - Clean up resources
    - Optionally close the app
    - Remove session

---

## 4. Window Attachment (Win32 + UIAutomation)

Reliable window attachment is essential.

### WindowFinder

Uses Win32 APIs:

    EnumWindows
    GetWindowThreadProcessId
    IsWindowVisible

Responsibilities:

- Enumerate top-level windows  
- Match windows by process ID or title  
- Return the correct HWND  

### AppLauncher

- Starts the target application  
- Waits for the main window  
- Hands off to WindowFinder  

### UIAutomationLayer

- Converts HWND → AutomationElement  
- Provides element search + interaction  
- Wraps System.Windows.Automation  

---

## 5. Element Location and Actions

### ElementLocator

Input:

- Strategy (AutomationId, Name, ClassName, ControlType)  
- Value  
- Search scope  

Output:

- AutomationElement or list of elements  

Implementation:

- Uses FindFirst / FindAll  
- Applies conditions  
- Handles retries + timeouts  

### ElementActions

Provides:

- Invoke (InvokePattern)  
- SetValue (ValuePattern)  
- Click (bounding rectangle + input simulation)  

Validates:

- Element exists  
- Required pattern supported  
- Errors mapped to WebDriver responses  

---

## 6. Screenshot Service

ScreenshotService captures window or element regions.

### Implementation Outline

- Use GDI:
      GetDC
      CreateCompatibleDC
      CreateCompatibleBitmap
      BitBlt
- Convert bitmap → PNG/JPEG  
- Return Base64 string  

Performance improvements can be added later.

---

## 7. WebDriver Protocol Model

### WebDriverModel

Defines DTOs for:

- Session creation  
- Element representation  
- Screenshot responses  
- Error responses  

### JsonResponseFactory

Centralizes:

- Status codes  
- Error messages  
- JSON formatting  

Ensures Appium compatibility.

---

## 8. Adding a New Command

Steps:

1. Add route in Minimal API.  
2. Create handler in Commands/Handlers.  
3. Use SessionManager to retrieve session.  
4. Use UIAutomationLayer, WindowFinder, ScreenshotService, etc.  
5. Return WebDriverModel response.  
6. Add tests.  
7. Update documentation.  

---

## 9. Error Handling and Diagnostics

Principles:

- Never expose raw exceptions  
- Map internal errors → WebDriver error types  
- Include clear messages  
- Log command name, session ID, element info  

Future improvements:

- Structured logging  
- Debug endpoints  
- Correlation IDs  

---

## 10. Extensibility Guidelines

When extending:

- Keep responsibilities separated  
- Avoid mixing protocol logic with automation logic  
- Prefer small, focused services  
- Avoid hard-coded values  
- Document new behaviors  

---

## 11. Development Workflow

Recommended workflow:

1. Clone repo  
2. Build with .NET 10  
3. Run WindowsDriverCore.Server  
4. Connect via Appium  
5. Implement changes  
6. Test manually + unit tests  
7. Commit small, focused changes  
8. Open PR  

---

## 12. Good First Contributions

- Add locator strategies  
- Improve error messages  
- Implement more WebDriver endpoints  
- Enhance screenshot handling  
- Write documentation  
- Add architecture diagrams  

---

This guide will evolve as the project grows. Contributors should update it whenever new patterns or subsystems are introduced.

# Windows WebDriver Replacement (WinAppDriver-Compatible) – Technical Design Document

## 1. Overview

This document defines the architecture, API, components, and implementation strategy for building a modern Windows WebDriver server in .NET 10 that is fully compatible with Appium’s Windows driver. The goal is to replace WinAppDriver.exe with a reliable, open-source alternative that fixes window-attach issues and improves automation stability.

The server will:

    - Listen on http://127.0.0.1:4723
    - Implement the WebDriver protocol and Windows-specific extensions
    - Use Win32 APIs for window enumeration and selection
    - Use UIAutomation for element discovery and actions
    - Return JSON responses in the exact shape Appium expects

---

## 2. Scope

### 2.1 In-Scope

    - GET /status
    - POST /session
    - DELETE /session/{sessionId}
    - POST /session/{sessionId}/element
    - POST /session/{sessionId}/element/{elementId}/click
    - POST /session/{sessionId}/element/{elementId}/value
    - GET /session/{sessionId}/screenshot

    - Session lifecycle management
    - Application launching (EXE, future UWP/MSIX)
    - Window discovery via Win32
    - Element automation via UIAutomation
    - WebDriver-compliant error responses
    - Integration tests using existing WinAppDriver test suite

### 2.2 Out-of-Scope (Initial Release)

    - Full WinAppDriver endpoint parity
    - Multi-window advanced scenarios
    - Non-Windows platforms
    - Remote execution or distributed mode

---

## 3. Architecture

### 3.1 Component Overview

    - WebDriverServer (Minimal API host)
    - CommandDispatcher
    - SessionManager
    - AppLauncher
    - WindowFinder
    - UIAutomationLayer
    - WebDriverModel (DTOs)
    - Error handling subsystem

---

## 4. HTTP API Specification

### 4.1 Base URL

    http://127.0.0.1:4723

### 4.2 Endpoints

#### GET /status

    {
        "value": {
            "ready": true,
            "message": "Windows WebDriver replacement is running"
        }
    }

#### POST /session

    {
        "capabilities": {
            "alwaysMatch": {
                "platformName": "Windows",
                "app": "C:\\Path\\To\\App.exe"
            }
        }
    }

#### DELETE /session/{sessionId}

    {
        "value": null
    }

#### POST /session/{sessionId}/element

    {
        "using": "accessibility id",
        "value": "LoginButton"
    }

#### POST /session/{sessionId}/element/{elementId}/click

    {
        "value": null
    }

#### POST /session/{sessionId}/element/{elementId}/value

    {
        "text": "hello world"
    }

#### GET /session/{sessionId}/screenshot

    {
        "value": "iVBORw0KGgoAAAANSUhEUg..."
    }

---

## 5. Core Data Structures

### 5.1 SessionContext

    public sealed class SessionContext
    {
        public string SessionId { get; init; }
        public int ProcessId { get; init; }
        public IntPtr MainWindowHandle { get; init; }
        public AutomationElement RootElement { get; init; }
        public IDictionary<string, object> Capabilities { get; init; }
    }

### 5.2 SessionManager

    - CreateSession(capabilities)
    - GetSession(sessionId)
    - RemoveSession(sessionId)

---

## 6. WindowFinder Design

### 6.1 Algorithm

    1. Launch app and obtain process ID.
    2. Enumerate top-level windows using EnumWindows.
    3. Filter windows by process ID and visibility.
    4. Select main window (foreground preferred).
    5. Return HWND.

### 6.2 Win32 Interop Signatures

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

---

## 7. UIAutomationLayer Design

### 7.1 Root Element

    var root = AutomationElement.FromHandle(mainHwnd);

### 7.2 Element Search

    var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, id);
    var element = root.FindFirst(TreeScope.Descendants, condition);

### 7.3 Click

    if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
    {
        ((InvokePattern)pattern).Invoke();
    }

### 7.4 Send Keys

    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
    {
        ((ValuePattern)pattern).SetValue(text);
    }

---

## 8. Error Handling

### 8.1 WebDriver Error Format

    {
        "value": {
            "error": "no such element",
            "message": "Element not found with locator accessibility id = LoginButton",
            "stacktrace": ""
        }
    }

### 8.2 Exception Mapping

    - ElementNotFoundException → no such element
    - InvalidArgumentException → invalid argument
    - SessionNotFoundException → invalid session id
    - Generic exceptions → unknown error

---

## 9. Testing Strategy

### 9.1 Reuse WinAppDriver Tests

    - Start this driver instead of WinAppDriver.exe
    - Use identical capabilities
    - Validate JSON shapes and behaviors

### 9.2 New Tests

    - WindowFinder unit tests
    - SessionManager lifecycle tests
    - UIAutomation locator tests
    - Full integration tests with MAUI/WinUI sample app

---

## 10. Deployment & Usage

### 10.1 Binary

    WindowsDriverCore.exe

### 10.2 Appium Integration

    platformName: Windows
    automationName: Windows
    app: path to app

---

## 11. Unsafe Code Usage (Performance-Critical Areas)

### 11.1 Screenshot Capture

    - Direct pointer access
    - Fast memory copying
    - Eliminates bounds checks
    - Reduces screenshot time significantly

### 11.2 Win32 Interop Structs

    - Exact memory layout
    - Fixed buffers
    - Pointer fields
    - Reduced marshaling overhead

### 11.3 HWND Enumeration

    - Reduced delegate allocation
    - Lower GC pressure
    - Faster callbacks

### 11.4 Areas Avoiding Unsafe

    - HTTP server
    - JSON serialization
    - UIAutomation COM calls
    - Session management
    - WebDriver handlers

### 11.5 Summary

Unsafe code is used only in places it gives real performance:

    - WindowsDriverCore/Win32/
    - WindowsDriverCore/Automation/Screenshots/

All other components remain safe C#.

---

## 12. Future Enhancements

    - Full WinAppDriver endpoint parity
    - Multi-window support
    - Multiple sessions per app
    - Rich diagnostics
    - Configurable window selection strategies
    - UWP/WinUI-specific behaviors
    - Optional gRPC or named pipe control channel

---

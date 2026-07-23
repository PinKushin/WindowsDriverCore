# WindowsDriverCore  
A modern, reliable, open‑source Windows automation driver built for Appium and .NET 10.

WindowsDriverCore is a next‑generation WebDriver server for automating native Windows applications. It is designed as a clean, stable, actively maintained successor to WinAppDriver — built with modern .NET, predictable Win32 window attachment, and a modular architecture that developers can extend and contribute to.

---

## Badges

![.NET 10](https://img.shields.io/badge/.NET-10-blue)
![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6)
![License: MIT](https://img.shields.io/badge/License-MIT-green)
![PRs Welcome](https://img.shields.io/badge/PRs-Welcome-brightgreen)
![Status: Early Development](https://img.shields.io/badge/Status-Early%20Development-orange)

---

## Why WindowsDriverCore Exists

WinAppDriver was a great idea — but it is no longer actively maintained, and its architecture makes certain reliability issues nearly impossible to fix cleanly. WindowsDriverCore exists to provide:

- A fully open‑source, community‑driven replacement for WinAppDriver  
- Predictable window attachment using modern Win32 enumeration  
- A clean, modular UIAutomation layer  
- High‑performance screenshot and element operations  
- Full Appium Windows driver compatibility  
- A foundation that can evolve with Windows and .NET  

If you automate Windows apps, this project is built for you.

---

## Project Status

WindowsDriverCore is currently in **Milestone 1 (Early Development)**.

The core WebDriver server, session management, window attachment, and basic UIAutomation element interactions are being implemented. Contributions are welcome at this stage.

---

## Features

### Current

- Minimal API WebDriver server (HTTP, port 4723)
- Win32‑based window discovery and attachment
- UIAutomation element search (AutomationId, Name, ClassName)
- Element actions (Invoke, Value, Click)
- Screenshot capture (GDI BitBlt)
- WebDriver‑compliant JSON responses
- Session lifecycle management

### In Development

- Additional locator strategies (XPath, ControlType, Name)
- Improved screenshot performance using unsafe memory blocks
- Rich diagnostics and error reporting
- Full WinAppDriver endpoint parity

### Future Roadmap

- Multi‑window support
- Multiple concurrent sessions
- WinUI/UWP/MSIX app launching
- Optional gRPC or named pipe transport
- Plugin system for custom automation behaviors

See `docs/roadmap.md` for the full roadmap.

---

## Quick Start

### Launch the driver

    WindowsDriverCore.exe

### Appium configuration

    {
      "platformName": "Windows",
      "automationName": "Windows",
      "app": "C:\\Path\\To\\YourApp.exe"
    }

Appium will automatically connect to:

    http://127.0.0.1:4723

### First test example (pseudo‑code)

Start your Appium session and interact with elements using AutomationId, Name, or ClassName.

---

## Architecture Overview

WindowsDriverCore is built around a clean, modular architecture:

- **WebDriverServer** — Minimal API host  
- **CommandDispatcher** — Routes WebDriver commands  
- **SessionManager** — Tracks active automation sessions  
- **AppLauncher** — Starts and attaches to Windows apps  
- **WindowFinder** — Win32‑based HWND discovery  
- **UIAutomationLayer** — Element search and interaction  
- **ScreenshotService** — High‑performance GDI capture  
- **WebDriverModel** — DTOs for protocol compliance  

See `docs/technical-design.md` for the full architecture specification.

---

## Contributing

Contributions are welcome — especially from developers who:

- use Appium for Windows automation  
- have experience with Win32, UIAutomation, or .NET  
- want to help build a modern, stable automation stack for Windows  

Please read `CONTRIBUTING.md` before submitting a PR.

Ways to contribute:

- Implement WebDriver endpoints  
- Improve reliability and diagnostics  
- Enhance UIAutomation support  
- Write documentation  
- Build sample apps  
- Help with testing  

---

## Community

- Issues: GitHub Issues  
- Discussions: GitHub Discussions (if enabled)  
- Roadmap: `docs/roadmap.md`  

---

## Documentation

All project documentation lives in:

`/docs/`

Key documents:

- `technical-design.md`  
- `roadmap.md`  
- `implementation-guide.md`  
- `contributing.md`  

## Developer Testing (Optional)

WindowsDriverCore aims to be fully compatible with the original WinAppDriver WebDriver protocol.  
To validate compatibility, contributors can run the official WinAppDriver test suite against this driver.

The test suite is available in the original WinAppDriver repository:

- https://github.com/microsoft/WinAppDriver

The suite targets .NET Framework and must be retargeted to .NET Framework 4.8 before running.  
Once retargeted, the tests can be added to your solution and executed against WindowsDriverCore by pointing the driver URL to:

    http://127.0.0.1:4723

Running the suite is optional but strongly recommended for contributors implementing new WebDriver endpoints.

---

## License

WindowsDriverCore is licensed under the MIT License.

---

## Why This Project Matters

Windows automation is critical for testing enterprise applications, internal tools, and legacy systems. With WinAppDriver unmaintained, the ecosystem needs a modern, reliable, open‑source driver that developers can trust and contribute to.

WindowsDriverCore aims to be that foundation.

---

## Join the Project

If you automate Windows apps and want a stable, modern, open‑source driver — you’re in the right place.  
Stars, forks, issues, and PRs are all welcome.

Let’s build the future of Windows automation together.

# 📚 WindowsDriverCore Master Documentation Index (Current Source of Truth)

***
**Last Updated:** 2026-08-03
**Project Status:** Early Development (Milestone 1). Building a reliable, open-source replacement for WinAppDriver using raw COM interop in .NET 10.
**Primary Endpoint:** `http://127.0.0.1:4723`

---

## ✨ Quick Summary & Overview

This index consolidates knowledge from all project documentation files. For detailed, step-by-step technical specifications or architectural decisions, please refer to the linked guides below.

**Project Goal:** To provide a modern, reliable, and fully open-source Windows automation driver that solves stability issues inherent in legacy tools like WinAppDriver.
**Core Mechanism:** Direct use of **raw COM interop (`IUIAutomation`)** via `IntPtr` pointers to eliminate marshaling failures associated with managed wrappers.

---

## 🏛️ Architectural & Design Principles (Read for Architects)
*Location: windowsdrivercore/WindowsDriverCore/docs/ARCHITECTURE/*
This section defines *why* we build things the way we do and outlines the high-level design contracts.

### 📖 [technical_design.md](ARCHITECTURE/technical-design.md) - The Blueprint
Defines the scope of endpoints (`GET /status`, `POST /session`, etc.), the necessary API parity with Appium, and the core component interactions (Server $\to$ Dispatcher $\to$ Handlers).

### 🤖 [AI_CONTEXT.md](ARCHITECTURE/AI_CONTEXT.md) - System Context
Details the project's current state, its commitment to **IntPtr Everywhere**, and the "Cheat-Tool-Level Control" philosophy: raw COM access for maximum control over the automation process.

---

## 🛠️ Development Guides & Technical Specifications (Read for Implementers)
*Location: windowsdrivercore/WindowsDriverCore/docs/GUIDES/*
This folder contains detailed, actionable guides on implementing specific features and solving complex technical hurdles.

### ➡️ [index.md](GUIDES/index.md) - Master Guide List
*(This file serves as the primary Table of Contents for all guides.)*

---

## 🔬 Feature Deep Dives (The "How-To" Guides)
These documents solve specific, complex technical problems within the Windows ecosystem:

### COM & Interop Handling
*   **[001-intptr-com-migration.md](GUIDES/001-intptr-com-migration.md):** **CRITICAL.** Outlines the mandate to use `IntPtr` for all COM out parameters, fixing marshaling errors from managed wrappers.
*   **[007-com-exception-handling.md](GUIDES/007-com-exception-handling.md):** Specifies how low-level COM exceptions must be mapped and wrapped into standard WebDriver error responses.

### UI Automation & Element Interaction
*   **[005-pattern-access.md](GUIDES/005-pattern-access.md):** Details the required UIA patterns, including fallback strategies for element discovery (e.g., pattern interface definitions).
*   **[004-element-identity.md](GUIDES/004-element-identity.md):** Defines the primary strategy for uniquely identifying elements at runtime (`RuntimeId`), crucial for stable selectors.

### Protocols & Formats
*   **[006-w3c-vs-json-wire-protocol.md](GUIDES/006-w3c-vs-json-wire-protocol.md):** Critical guide detailing how the system must handle ambiguity between W3C standards and proprietary JSON Wire Protocol endpoints (e.g., `/rect` endpoint).
*   **[010-location-size-response-format.md](GUIDES/010-location-size-response-format.md):** Dictates the precise JSON structure required for location/size return values, ensuring compatibility with `WebDriverResponse`.

### Testing & Reliability
*   **[003-test-strategy.md](GUIDES/003-test-strategy.md):** Outlines the dual test suite approach (WinAppDriver compat + custom edge cases).
*   **[008-css-selector-and-appium-bugs.md](GUIDES/008-css-selector-and-appium-bugs.md):** Captures known bugs and mapping rules for CSS selectors that deviate from Appium's expectations (e.g., dot-prefix).
*   **[002-cheat-tool-approach.md](GUIDES/002-cheat-tool-approach.md):** Defines the philosophy of *not* hiding behavior, emphasizing raw control over abstracted APIs.

### General Concerns
*   **[010-location-size-response-format.md](GUIDES/010-location-size-response-format.md)**: (Duplicate listing for completeness) Focuses on ensuring correct JSON serialization for geometric data.

***
**Action Required:** To contribute, read this index first to understand the architectural context and then refer to the specific guide file for implementation details.
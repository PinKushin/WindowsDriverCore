# WindowsDriverCore — Memory Index

| File | Topic | Date |
|------|-------|------|
| [001-intptr-com-migration.md](001-intptr-com-migration.md) | Use IntPtr everywhere for COM out params, not just hwnd | 2026-08-02 |
| [002-cheat-tool-approach.md](002-cheat-tool-approach.md) | Cheat-tool-level control: raw COM, no hidden behavior, use unsafe | 2026-08-02 |
| [003-test-strategy.md](003-test-strategy.md) | Two test suites (WinAppDriver compat + custom edge cases), separate CI | 2026-08-02 |
| [004-element-identity.md](004-element-identity.md) | RuntimeId as element ID, stale detection strategy | 2026-08-02 |
| [005-pattern-access.md](005-pattern-access.md) | UIA patterns via COM, pattern interface definitions, fallback order | 2026-08-02 |
| [006-w3c-vs-json-wire-protocol.md](006-w3c-vs-json-wire-protocol.md) | Two-protocol trap: /rect endpoint, DualGetPost, W3C vs JWP endpoint mapping | 2026-08-02 |
| [007-com-exception-handling.md](007-com-exception-handling.md) | COMException → WebDriver error mapping, AllowStatusCode404Response | 2026-08-02 |
| [008-css-selector-and-appium-bugs.md](008-css-selector-and-appium-bugs.md) | CSS selector dot-prefix mapping, locator strategy reference, XPath error format | 2026-08-02 |
| [009-test-progress.md](009-test-progress.md) | 80/290 tests passing, failure categories, next steps | 2026-08-02 |
| [010-location-size-response-format.md](010-location-size-response-format.md) | JSON serialization fix, /rect response, Results.Json vs WebDriverResponse | 2026-08-02 |
| [011-click-semantics-and-the-coordinate-trap.md](011-click-semantics-and-the-coordinate-trap.md) | Why pattern-first Click is validated field evidence; TogglePattern missing; SetFocus fallback fails silently; MAUI needs a GUARDED mouse fallback | 2026-08-06 |
| [012-field-notes-from-driving-a-real-maui-app.md](012-field-notes-from-driving-a-real-maui-app.md) | Phantom elements with null ids, cached-snapshot misses (#1079 reproduced), WinAppDriver DOES auto-scroll inconsistently, MAUI Picker popups are separate windows, implicit wait charged in full for absent elements, GetText platform divergence | 2026-08-06 |

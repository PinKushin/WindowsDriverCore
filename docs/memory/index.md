# WindowsDriverCore — Memory Index

> **Start with [`docs/PROJECT-KNOWLEDGE.md`](../PROJECT-KNOWLEDGE.md).** As of 2026-08-08 every
> entry below is consolidated there — the protocol contract, compatibility floor, measured test
> results, UIA/COM knowledge, Win11 app drift, and the defects and dead theories not to repeat.
> That file is the one to read before writing code, and it is mirrored outside the repo so it
> survives the rewrite.
>
> The entries below are kept as the audit trail: they show what was believed when, and several
> record corrections to their own earlier claims. Useful for "why did we conclude that?", not as
> the current specification. Where an entry and `PROJECT-KNOWLEDGE.md` disagree, the latter wins.

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
| [013-architecture-audit.md](013-architecture-audit.md) | Full-source audit: shutdown cleanup never runs (DI bug), Close() kills all same-named processes, ClickAt ignores coordinates, /keys injects nothing (INPUT struct is 32 bytes, needs 40), /window/handles self-inconsistent, measured DRY/SOLID violations, PowerShell command injection, why NOT to restart | 2026-08-08 |
| [016-win11-app-drift-catalogue.md](016-win11-app-drift-catalogue.md) | Every Win11 difference vs what the WinAppDriver suite expects — Alarms dialog renames, Calculator header text, Notepad RichEditD2DPT, legacy Edge is gone. Kept because the WinAppDriver tree is now pristine | 2026-08-08 |
| [015-winappdriver-protocol-contract.md](015-winappdriver-protocol-contract.md) | THE SPEC: full 60-route JWP table, error envelope with numeric status, locator semantics (tag name = LocalizedControlType not ControlType id), capabilities, CLI forms. window_handle is missing and causes per-test app relaunch | 2026-08-08 |
| [018-window-lifetime-xpath-and-what-a-find-costs.md](018-window-lifetime-xpath-and-what-a-find-costs.md) | A session's window is not permanent (a CoreWindow is top-level at launch and destroyed on rehost); a dead window must fail fast but only when the process is really gone; XPath uses System.Xml.XPath over a per-request projection because the flakiness was the snapshot's lifetime, not the evaluator; and what a find actually costs - traversal dominates, children scope 8.6x, early exit 16x, cache wins at five properties and loses at one | 2026-08-10 |
| [017-suite-environment-contamination.md](017-suite-environment-contamination.md) | A score is not a property of the driver alone. The suite fills Alarms & Clock to its cap, which disables ONLY "Add new alarm" and costs exactly nine tests; resetting the store cold-starts the app and costs sixteen more. Reset AND warm. Also: the disabled-click bug this exposed, and why six "StaleElement" tests never test staleness | 2026-08-10 |
| [014-compatibility-and-deployment-targets.md](014-compatibility-and-deployment-targets.md) | Floor is Win10 1607 / Server 2016 — same as WinAppDriver, .NET is not the limiter. Real gaps: no /wd/hub, no CLI args, loopback-only, Desktop Runtime prerequisite. GitHub runners are 2022/2025/11-arm; windows-2019 is gone | 2026-08-08 |

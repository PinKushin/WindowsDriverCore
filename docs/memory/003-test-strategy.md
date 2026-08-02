# 003 — Test Strategy

## Date: 2026-08-02

## Two Test Suites, Separate CI

### WinAppDriver Test Suite (Compatibility Contract)
- **418 tests** across 4 projects (AbsoluteXPath, Input, UWPControls, WebDriverAPI)
- Must ALL pass — this is the compatibility guarantee
- Run against our server at `http://127.0.0.1:4723`
- Separate CI workflow for visibility
- These tests define the behavioral contract we must honor

### Custom Win32 Test Suite (Edge Cases)
- Our own tests targeting the custom TestApp (Win32 program)
- Tests things WinAppDriver tests don't cover
- Edge cases: stale elements, rapid re-find, concurrent sessions, property reads on complex elements
- Also separate CI workflow
- Expands our test surface beyond WinAppDriver's coverage

## Why Separate
- If our custom tests fail, it shouldn't block the WinAppDriver compatibility signal
- If WinAppDriver tests fail, we know we regressed the contract
- Independent signals = faster debugging

## CI Setup
- Each test suite gets its own GitHub Actions workflow
- Both need the server running (dotnet run) as a service step
- Both need the TestApp built and available

## Test App
- Custom Win32 program at `TestApp/` — NOT a copy of WinAppDriver's test apps
- Windows 11 lacks a good native Win32 test target, so we built one
- Keep it — don't remove it when WinAppDriver tests pass
- Expand it with edge cases that test our raw COM implementation specifically

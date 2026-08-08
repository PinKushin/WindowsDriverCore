# 013 — Architecture Audit

## Date: 2026-08-08

Full read of all 34 source files (3,782 lines). The codebase was touched by several different AI
sessions over ~2 weeks with no test suite to hold anything in place, and it shows: the *shape* is
right, but the stated design decisions and the actual behaviour have drifted apart in specific,
findable ways. This entry is the inventory. Nothing here is speculative — every item cites a line.

## Defects that change behaviour

### 1. `KillAllTrackedProcesses` never runs — DI registration mismatch

`Program.cs:31` registers `builder.Services.AddHostedService<SessionCleanupService>()`, which
registers the type **only** as `IHostedService`. `Program.cs:38` then does
`app.Services.GetRequiredService<SessionCleanupService>()`, which cannot resolve and throws at
shutdown. So the shutdown hook that kills tracked processes never executes.

This is why `CLAUDE.md` carries a manual "kill orphaned Calculator/Alarms windows" PowerShell
snippet — it is papering over a one-line DI bug. Fix: also register the concrete type and forward
the hosted service to it.

### 2. `AppLauncher.Close()` kills every process sharing the name

`AppLauncher.cs:48-52`:

```csharp
if (processName != null)
{
    KillAllProcessesByName(processName);
    return;
}
```

This is the **first** branch for every non-Explorer process, and it returns. Ending a Notepad
session kills every Notepad the user has open, including ones the driver never launched.

The 5-step graceful cascade documented in `CLAUDE.md` (WM_CLOSE → taskkill /F /T → bottom-up →
`Kill(entireProcessTree)`) lives at `AppLauncher.cs:54-88` and is **unreachable** — it only runs
when `GetProcessNameById` returns null, i.e. the process is already dead. Documentation and
behaviour have completely diverged. The multi-process-browser case that motivated the by-name kill
is real, but it should be a narrow branch, not the default.

### 3. `ClickAt(elementId, x, y)` silently ignores its coordinates

`ElementInteractor.cs:244` — the whole body is `Click(elementId);`. Every coordinate-based click
lands wherever the pattern-based click lands. Callers get no signal.

### 4. `/window/handles` returns handles that `/window` then refuses

`SessionRoutes.cs:290` enumerates **child** windows via `EnumChildWindows` and reports them as
window handles. `SessionRoutes.cs:354` rejects any switch target that is not top-level. So the
driver advertises handles it will not accept. Directly implicates the failing `Window.SwitchWindows`
tests.

Compounding it: `WindowFinder.FindNewApplicationFrameWindow` (`WindowFinder.cs:228`) returns the
**CoreWindow** when it can and the ApplicationFrameWindow otherwise. A CoreWindow's parent is the
frame window, so a session created down that path holds a `MainWindowHandle` that fails the
driver's own top-level check. Window-handle identity needs one rule, stated once.

### 5. `/timeouts` response envelope is discarded

`SessionRoutes.cs:470-478` — the lambda is `async (...) => { await HandleTimeoutRequest(...); }`,
returning `Task`, not `Task<IResult>`. `HandleTimeoutRequest` builds a proper
`WebDriverResponse` and it is thrown away; the endpoint returns a bare 200.

### 6. `/keys` injects nothing — `INPUT` struct is the wrong size

`Win32.InputUnion` (`Win32.cs:180`) declares only `KEYBDINPUT` at offset 0. The real Win32 `INPUT`
union must be sized for its largest member, `MOUSEINPUT`. Measured on this machine:

```
repo INPUT struct size    : 32
correct INPUT struct size : 40   (x64)
```

`SendInput` validates `cbSize` against its own `sizeof(INPUT)` and fails with
`ERROR_INVALID_PARAMETER` on mismatch. `KeyboardSimulator.cs:29` passes
`Marshal.SizeOf<Win32.INPUT>()` = 32, so **every call fails and zero keystrokes are injected**. The
return value is discarded, so it fails silently — also a direct violation of the "return-value error
signals must always be checked" rule. On x86 the numbers differ (20 vs 28) but it is equally broken.

### 7. `CharToVirtualKey` is wrong for nearly every key

`KeyboardSimulator.cs:45`. Independent of #6, the table would not work:

- Lowercase letters map to `ch - 'a' + 0x61`. `0x61` is **`VK_NUMPAD1`**, not `A`. Typing "a"
  presses numpad-1. Letters are `0x41 + (ch - 'a')` regardless of case, with `VK_SHIFT` held for
  uppercase — and uppercase is currently mapped without any shift, so it would type lowercase.
- The Selenium private-use-area table is off by one from `` onward and then diverges entirely.
  Real map: `` Pause, `` Escape, `` Space, `` PageUp, `` PageDown,
  `` End, `` Home, ``–`` arrows, `` Insert, `` Delete,
  ``–`` Numpad0–9, ``–`` F1–F12. The file assigns F1–F12 to
  ``–`` (actually the numpad range) and leaves the real F-key range unmapped, so it
  falls through to `_ => 0` and is silently dropped.
- `` Backspace is mapped to `0x20` (space) with a comment admitting it. Backspace is `0x08`.

The right fix is not to repair the table: use `KEYEVENTF_UNICODE` with `wVk = 0` and
`wScan = ch` for printable text — layout-independent and correct for everything outside the PUA
range — and keep an explicit virtual-key table only for the `\uE0xx` control keys.

Note the interaction with `009`: `SendKeys` was written off as an unfixable client-side crash in the
old Appium driver. That may be true, but it masked the fact that the server side does not work
either. Nothing has ever verified `/keys` end to end.

### 8. Implicit wait is accepted and ignored

See `009`. `/timeouts` validates and discards; `ElementFinder` has no retry loop. Root cause of the
167-test `AlarmClockBase` block.

## Design-standard violations

Measured, not impressions:

| Standard | Violation | Count / location |
|---|---|---|
| DRY | `DualGetPost` defined twice | `SessionRoutes.cs:17`, `ElementRoutes.cs:28` |
| DRY | Session-lookup-or-throw inlined instead of shared | 23× in `ElementRoutes`, 2× in `SessionRoutes` (which *has* `GetSessionOrThrow`) |
| DRY | `"Currently selected window has been closed"` literal | 13× |
| DRY | Stale-element message literal | 12× |
| DRY | COMException→WebDriver mapping (`0x80040200` switch) | 4 sites: `Program.cs:74`, `ElementInteractor.cs` (×5 copies inline), `ComConstants` |
| DRY | Screenshot capture inlined in two route handlers | `SessionRoutes.cs:242`, `ElementRoutes.cs:333` |
| DRY | Hex/decimal window-handle parsing + error strings | `SessionRoutes.cs:325`, `:584` |
| DRY | ControlType id→name map duplicated as magic numbers | `RawAutomationElement.cs:73` vs `ComConstants.cs` |
| No silent catch | `catch { }` | **34 occurrences** — heaviest in `AppLauncher` (11) and `SessionCleanupService` (5) |
| No magic strings repeated | `"ApplicationFrameWindow"`, `"CabinetWClass"` etc. re-literalled in `SessionRoutes` despite constants existing in `WindowFinder` | `SessionRoutes.cs:653,656,729` |

### DIP — abstractions exist but are bypassed

- `IScreenshotCapture` (`Screenshots/IScreenshotCapture.cs`) has **zero implementations and zero
  usages**. Pure dead abstraction; the logic it describes is inlined in two routes.
- `Win32` is a static class called **directly from route handlers** (`GetWindowRect`, `ShowWindow`,
  `PostMessage`, `EnumChildWindows`, `GetDesktopWindow`) even though `IWindowFinder` is injected
  right beside it. Routes cannot be tested without a real desktop.
- `KeyboardSimulator` is static with no interface (`SessionRoutes.cs:499`).
- `AppLauncher.IsDesktopAppId` is `internal static` on the concrete class and called from routes in
  4 places, bypassing `IAppLauncher`.
- `UIAutomationFactory` is a static singleton with a **non-thread-safe** lazy init
  (`UIAutomationFactory.cs:12`); `ElementFinder`'s constructor calls it rather than receiving
  `IUIAutomation`.
- `ConditionFactory` is static mutable global state, initialised as a side effect of constructing
  `ElementFinder` (`ElementFinder.cs:17`). Two consumers means an ordering hazard.

### ISP / layering

- `IElementInteractor` is one 14-method interface mixing actions (`Click`, `SendKeys`, `Clear`) with
  property reads. Nothing consumes only half of it because nothing can.
- `GetSelected`, `GetCoordinates`, `GetSize`, `GetLocationInView` return **JSON strings**. The
  routes then `JsonDocument.Parse` them and re-serialize (`ElementRoutes.cs:239-249`). The
  automation layer knows about the wire format; the transport layer does string surgery. That is
  the single biggest obstacle to the layer being reusable outside this HTTP server.

### Nullability / construction

- `RawAutomationElement` uses `_element!` on **every** member (25+ sites) while also exposing an
  `Element` property that does the check properly. The null-forgiving operator is doing the work a
  guard should. Consequence: after `Dispose()`, `IsAlive()` (`:179`) throws
  `NullReferenceException`, which its `catch (COMException)` does not catch.
- `SessionContext` uses constructor assignment, not `required` members.
- `ErrorType.NoSuchSession` and `ErrorType.InvalidSessionId` are two constants with the identical
  value `"invalid session id"`.

### Resource lifetime

- `ElementStore.Store` (`ElementStore.cs:13`) overwrites an existing key without disposing the old
  wrapper. Re-finding the same element leaks its RCW. Nothing ever disposes stored elements or
  clears the store per session — element ids are global RuntimeIds with no session scoping.

## Security

`AppLauncher.FindAumidForExe` (`AppLauncher.cs:355`) builds a PowerShell command by string
interpolation from the `app` capability:

```csharp
Arguments = $"-Command (Get-AppxPackage -Name '*{exeBase}*' | Select-Object -First 1).PackageFamilyName"
```

`exeBase` is attacker-influenced. Windows filenames may contain single quotes, so a file named
`a'; <command>; '.exe` breaks out of the quoting. Mitigating factors: the path must exist on disk
(`ResolveAppPath` requires `File.Exists`), and the server binds `127.0.0.1` only. Still a genuine
command-injection vector and a direct violation of the project's own OWASP rule. The right fix is
the `PackageManager` WinRT API rather than shelling out at all.

## Things that are actually fine

Worth recording so future sessions don't churn on them:

- `SessionCleanupService` is the best-written file — real `ILogger`, specific catches at the loop
  boundary, correct cancellation handling.
- `WindowFinder`'s Win32-then-UWP strategy, and the owned/unowned candidate preference
  (`WindowFinder.cs:130-163`), is sound and hard-won.
- `WaitForMainWindow`'s pre-launch snapshot of frame/cabinet windows (`SessionRoutes.cs:648`) is a
  genuinely good idea for slow-starting and shell-reusing apps.
- `Messages/*` records are clean.
- The `DualGetPost` dual-protocol trick works and is the right call (see `006`).
- The build is warning-free apart from one CS0169 in the test project.

## Observability gap

`ILogger` is referenced in exactly one file. Three `Console.WriteLine` calls do the rest, including
a debug line left in a hot path (`SessionRoutes.cs:386`, `[GetWindowRect]`) and unconditional
per-request logging in `Program.cs:45`. `012` argued that window/element rects belong in the session
and error payloads — that cannot happen usefully until logging goes through `ILogger`.

## Assessment

Do not restart. The parts that are hard to rediscover — window discovery, UWP activation, the
dual-protocol surface, the process cascade's *intent* — are present and correct. What is missing is
a seam: everything is reachable statically from everywhere, so nothing can be tested, so nothing
holds the design in place between sessions. That is the actual root cause of the drift, and it is
also why `009` shipped a wrong diagnosis that survived a week.

Recommended order (each step makes the next one measurable):

1. Implicit wait in `ElementFinder`, written test-first. Re-baseline the 290.
2. Fix the two silent behaviour bugs: DI registration (#1) and by-name process kill (#2).
3. Introduce the seams — `IScreenshotCapture` implemented, `IWin32` behind `IWindowFinder`,
   `IUIAutomation` and `IConditionFactory` injected, `ConditionFactory` made non-static.
4. Collapse the duplication: one `GetSessionOrThrow`, one `ValidateWindow`, one `DualGetPost`, one
   COM→WebDriver mapper, one message-constants class.
5. Change `IElementInteractor` to return typed values, not JSON strings; split it along the
   action/property line.
6. Only then the `#857` / `#1079` work the project exists for.

Relates to [[009-test-progress]], [[011-click-semantics-and-the-coordinate-trap]],
[[012-field-notes-from-driving-a-real-maui-app]].

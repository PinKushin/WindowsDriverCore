# WindowsDriverCore — Rewrite Specification

**Status:** in progress on `feat/rewrite-jwp-core`. Milestones 1–6 partially done —
see `CLAUDE.md` for what works and `LIMITATIONS.md` for what does not.
**Date:** 2026-08-08
**Supersedes:** the current `WindowsDriverCore/` implementation (kept as reference until parity)

Background research lives in `docs/memory/` — `013` (architecture audit), `014` (compatibility
floor), `015` (the protocol contract), `009` (measured test results). This document is the build
plan. It does not repeat the evidence; it states the decisions.

---

## 1. Why rewrite rather than repair

The abstractions that are wrong are load-bearing and touch every file:

- `IElementInteractor` returns **JSON strings**; routes `JsonDocument.Parse` and re-serialize them.
  The automation layer knows the wire format.
- `Win32`, `KeyboardSimulator`, `ConditionFactory`, `UIAutomationFactory` are static and reachable
  from anywhere, including route handlers. Nothing can be tested without a real desktop.
- The route table was derived from the **W3C WebDriver spec**. The contract is **JSON Wire
  Protocol**. That is not a bug list, it is a wrong foundation.

Repairing these means editing essentially every file, so the refactor diff is the rewrite diff —
except a refactor silently carries forward decisions nobody can see. At 3,782 lines the rewrite is
not the expensive part. **The knowledge was the expensive part and it is already captured.**

**The one condition:** this only pays if it is test-first. The root cause of three weeks of drift
was no seams → no tests → nothing holding the design between sessions. A rewrite done the same way
lands in the same place.

---

## 2. The contract

**JSON Wire Protocol.** Stated explicitly by `WinAppDriver/Tests/WebDriverAPI/README.md`. Where JWP
and W3C disagree, JWP wins. The complete 60-route table is in `docs/memory/015`; it is the
acceptance contract and the source of the test list.

Non-negotiables that the current implementation gets wrong:

| Thing | Correct behaviour |
|---|---|
| `GET /session/:id/window_handle` / `window_handles` | must exist — underscore form, not `/window/handle` |
| Error envelope | `{"status":<int>,"value":{"error":…,"message":…}}` — numeric top-level `status` |
| Unknown session | bare HTTP 404, **empty body** |
| `GET /status` | unwrapped `{build,os}` — no `value` wrapper |
| `tag name` locator | matches **LocalizedControlType** (upper camel case string), not ControlType id |
| unknown `tag name` | must **throw**, never fall back to Custom |
| Error message strings | take verbatim from `CommonTestSettings.cs` → `ErrorStrings` |
| CLI | `WinAppDriver.exe [port]` / `[ip] [port]` / `[ip] [port]/wd/hub`; `*` binds all |
| Base path | one mount via `UsePathBase`, configured at startup — **not** dual-mounted |

Capabilities: `app` (incl. `"Root"`), `appArguments`, `appTopLevelWindow` (hex), `appWorkingDir`,
`platformName`, `platformVersion`.

---

## 3. Compatibility target

Floor is **Windows 10 1607 / Windows Server 2016** — the same floor WinAppDriver states. .NET 10
reaches further back than that, so the framework is not the constraint.

- Drop `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />`; use the
  `System.Drawing.Common` package. Removes the Desktop Runtime prerequisite.
- Ship self-contained for `win-x64`, `win-x86`, `win-arm64`. `windows-11-arm` is a real GitHub
  runner, so arm64 is a first-class target — an area to beat WinAppDriver, not just match it.
- **Native AOT is out**: `Marshal.ReleaseComObject` and built-in COM interop are unsupported under
  AOT. Not worth a `ComWrappers` rewrite. JIT is also the right call on merit — this workload is
  dominated by cross-process COM round-trips, not codegen.
- CI matrix: `windows-2022`, `windows-2025`, `windows-11-arm`. `windows-2019` no longer exists.

---

## 4. Architecture

### Layering rule

**The automation layer must not know that HTTP or JSON exist.** It returns typed values. The
transport layer owns serialization. This single rule kills the worst defect in the current code.

```
WindowsDriverCore.Protocol      HTTP surface. Routes, JWP envelope, error mapping. No UIA.
WindowsDriverCore.Automation    UIA element find/interact. Typed in, typed out. No HTTP.
WindowsDriverCore.Platform      Win32 P/Invoke, window discovery, input, process lifetime.
WindowsDriverCore.Host          Composition root, CLI parsing, DI wiring, logging.
```

Dependencies point one way: `Host → Protocol → Automation → Platform`.

### Every dependency is an interface, resolved through DI

No statics reachable from a route handler. Specifically:

- `IWin32` wraps the P/Invoke surface — routes never call `Win32.*` directly.
- `IUIAutomation` is **injected**, not created in a constructor.
- `IConditionFactory` is an instance, not static mutable global state.
- `IKeyboardInput`, `IScreenshotCapture`, `IProcessController` all exist and all have exactly one
  real implementation plus a test double.
- `IScreenshotCapture` must be *implemented*, not declared and ignored.

### Interfaces, split by responsibility

`IElementInteractor`'s 14 methods become:

```csharp
IElementActions    { Click, ClickAt, SendKeys, Clear }
IElementProperties { GetText, GetName, IsEnabled, IsDisplayed, IsSelected,
                     GetTagName, GetAttribute, GetLocation, GetSize, GetRect }
```

Return types are `Point`, `Size`, `Rect`, `bool`, `string` — never JSON.

### Element identity and lifetime

- Element id stays the RuntimeId string (round-trips through `FindElementById`, so the separator is
  cosmetic).
- The element store is **scoped per session** and disposed with the session. The current global
  store leaks every RCW it overwrites.
- Distinguish three states explicitly: alive / stale / **identity-not-yet-resolvable** (the
  phantom-element case from `012`). The third maps to `stale element reference`, not to a
  half-formed element.

### Waiting

Implicit wait is a first-class concept, not an afterthought:

- Session holds the implicit-wait duration; `/timeouts` actually sets it.
- Find retries until the deadline, then throws.
- Support a per-request override so "does this exist right now" is cheap — absence currently costs
  the full timeout, which `012` measured at ~6.8 s per miss.

### Policy decisions to state once and document

The damage in WinAppDriver comes from these being conditional and undocumented:

- **Scroll-into-view on click**: pick one behaviour, apply it uniformly, state it.
- **`GetText`**: returns the value, or the name — pick one and write it down.
- **Popups and separate top-level windows**: explicit search policy, not emergent.
- **Click**: pattern-first with a *guarded* mouse fallback, per `011`. Never `SetFocus` as a silent
  last resort.

---

## 5. Harvest list

Take near-verbatim from the current implementation:

- `Win32.cs` P/Invoke declarations — **fix `InputUnion` to include `MOUSEINPUT`** so `INPUT` is
  40 bytes on x64 / 28 on x86. It is currently 32 and `SendInput` fails every call.
- `WindowFinder`'s Win32→UWP strategy and the owned/unowned candidate preference.
- `AppLauncher`'s `IApplicationActivationManager` activation and broker-PID resolution.
- `WaitForMainWindow`'s pre-launch snapshot of frame/cabinet windows.
- `Messages/*` records.
- Every error string.

Do **not** carry forward:

- `AppLauncher.Close`'s kill-all-by-name branch (kills unrelated user apps) or the unreachable
  5-step cascade behind it.
- `FindAumidForExe`'s PowerShell shell-out — command-injection vector; use the `PackageManager`
  WinRT API.
- `CharToVirtualKey`'s table — use `KEYEVENTF_UNICODE` for printable text plus a small explicit
  table for the `\uE0xx` control keys.
- `AddHostedService<T>` + `GetRequiredService<T>()` — register the concrete type and forward the
  hosted service to it, or shutdown cleanup silently never runs.

---

## 6. Test strategy

**Two suites, different jobs. Conflating them is what cost three weeks.**

### Inner loop — ours, fast, owned

- Unit tests against the interfaces above, with test doubles for `IWin32` / `IUIAutomation`.
- Protocol tests via `WebApplicationFactory` — assert exact JWP envelopes, status codes and error
  strings without touching a desktop. **One test per row of the 60-route table**, written before
  the route exists.
- A small integration suite against a **frozen app we control** —
  `WinAppDriver/ApplicationUnderTests/` ships AppUIBasics and Xaml-Controls-Gallery as buildable
  source. Frozen, versioned, ours. No Store app can drift under it.

### Acceptance — theirs, unmodified, occasional

The WinAppDriver `WebDriverAPI` suite is the compatibility scoreboard. Run it on demand, never as
the dev loop. Revert the three local modifications to `CalculatorBase.cs`, `SendKeys.cs` and
`AlarmClockBase.cs` so the scoreboard is honest.

The Alarms fixture repair (`CancelButton` → `CloseButton`, `AlarmSaveButton` → `PrimaryButton`) is
a separate, clearly-labelled harness fix — it unblocks ~167 tests that currently cannot be measured
at all. Track it as a fixture patch, not as a suite modification.

### Every test is an experiment, and it must test *our* hypothesis

A test has a **manipulation** (a deliberate change to the code), a **measurement** (the assertion),
a **condition** (the input) and ideally a **control** (a second subject that must be unaffected).
Code is deterministic, so predict an exact value and measure it. One decisive case falsifies;
there is nothing to average.

Before writing the assertion, ask in this order:

1. **Is there an input where correct and broken differ?** If not, the condition is wrong — fix the
   input, not the assertion.
2. **Does my assertion detect that difference?** If it measures a proxy, fix the instrument.
3. **Is there a bystander that must survive?** If not, "affected the target" and "affected
   everything" are indistinguishable.

Two real examples from this codebase, both caught by auditing rather than by a failing test:

- `ToListenUrl` was originally tested with the condition `4723/wd/hub` — but 4723 **is** the
  default port, so an implementation that ignored the parsed port and hardcoded the default
  produced the identical observation. Wrong condition. Changed to 4725, then verified by
  hardcoding the default on purpose: exactly one test failed, the right one.
- The invalid-port cases asserted only `Should.Throw<FormatException>`. Every rejection throws that
  type, so a validator that rejected *all* input — including valid ports — passed all five.
  Unfaithful instrument. Now the message is asserted, and a
  `Parse_ValidPortSpecification_DoesNotThrow` control was added, without which "rejects bad input"
  and "rejects everything" cannot be told apart.

Neither of these was a failing test. Both were tests that could not fail.

### The project-level hypotheses

The suite exists to test these, not just to exercise routes. Each is falsifiable and each needs the
control named beside it, or passing means nothing:

- **H1 — querying the live UIA tree eliminates the empty-`FindElements` class (#1079).**
  Experiment: build a tree, mutate it while querying, assert `FindElements` never returns empty for
  an element that is present. **Control: the same manipulation through WinAppDriver must reproduce
  the failure.** Without that control, a green test proves only that the scenario is hard to
  trigger, not that we fixed anything.
- **H2 — a pattern-first click with a guarded fallback eliminates off-window clicks.**
  Experiment: an element below the fold in a deliberately small window. Prediction: we either click
  the element or throw naming both rects — never dispatch input outside the window.
  **Control: a coordinate click at the same geometry must land outside.** Effect size matters here:
  the window has to be small enough that the difference appears at all (754x512 on 1920x1080 is a
  measured case that produced clicks on the taskbar).
- **H3 — the HTTP hop can approach FlaUI's in-process cost.**
  Measurement: BenchmarkDotNet, same operation, three subjects. FlaUI is the floor, WinAppDriver is
  the baseline to beat, and the gap between us and FlaUI is the budget.

### UI tests: the only legitimate uncertainty is *when*, never *what*

The program is deterministic and the measured values are deterministic. Only the moment a
measurement can be taken is uncertain, because layout, rendering and IPC take variable time. One
consequence, and it is not a licence for looseness: **synchronise on the condition, never on the
clock.** No `Thread.Sleep`, no "usually long enough". Never retry a failing test to make it pass —
that converts a deterministic failure into a probabilistic pass and destroys the signal. Flake is a
defect in synchronisation or in the app, never noise.

### Mutation testing is a ratchet, not a gate

`stryker-config.json` starts at `break: 0` — Stryker reports and never fails the build. The
80/60 high/low values only colour the report.

**These are not the numbers to aim at; they are the numbers we currently have.** Thresholds get
raised deliberately, at a milestone boundary, to just under whatever the suite actually scores at
that point. They are never lowered. Copying another project's mature thresholds (TcgDex sits at
95/90/85) into a greenfield tree fails the build on day one against code that barely exists, which
teaches everyone to ignore the tool.

Mutation testing is also close to meaningless until there is real logic to mutate — expect the
first useful run around milestone 3, once the error mapper and locator resolution exist. Read the
**surviving mutants**, not the score: a survivor is a change no test noticed. Ignore mutants that
are equivalent by construction, and do not write change-detector tests to kill one. Killing a
mutant is not the goal; noticing a real change is.

### Known ground truth

Measured 2026-08-08 against WinAppDriver 1.2.2009.02003 on Win11 26200:

- WinAppDriver: **112 / 290**. Our current build: 77 / 290.
- Our real backlog: **70 tests** that WinAppDriver passes and we fail.
- 143 fail for both, almost all downstream of the Alarms fixture defect.
- ~40 of the 70 are missing JWP routes plus request-payload validation. Mechanical.

---

## 6b. Post-parity feature — user-supplied locator alias map

**Not part of parity. Do not build before milestone 11.**

The problem it solves is real and unaddressed: a Windows app update renames its automation ids and
every test in a suite breaks at once. Win11's Clock is a live example — `CancelButton` became
`CloseButton`, `AlarmSaveButton` became `PrimaryButton`, and `AlarmNameTextBox` lost its id
entirely. WinAppDriver offered nothing for this and is archived, so a team hitting it today has no
migration path other than editing every test.

Shape:

```
WindowsDriverCore.exe --alias-map aliases.json
```

```json
{ "accessibility id": { "CancelButton":    "CloseButton",
                        "AlarmSaveButton": "PrimaryButton" } }
```

Rules that make this honest rather than a cheat:

- **Off by default.** No built-in alias table ever ships. The map is the user's, describing their
  app, and lives in their repo.
- **Applied only after a genuine miss** — never rewrite a locator that already resolves.
- **Never silently.** `GET /status` reports that aliasing is active and how many entries are loaded;
  a response for an element resolved via an alias marks it as such. A find and a subsequent
  `GetAttribute("AutomationId")` must never quietly disagree.
- **Never used to score.** The compatibility suite is run with aliasing **off**. A number produced
  with aliasing on is a different measurement and must be labelled as one.

The reason this cannot be the fix for the Alarms block: the old suite has no way to request it. It
just sends `accessibility id: CancelButton`. Activation would have to be either implicit — the
driver sniffing the app and rewriting behind the caller's back, which is the worst possible
option — or via a flag the suite never sets, which does nothing for the score. Fix the fixture for
that; ship aliasing for users.

## 7. Build order

Each milestone ends green and measurable.

1. **Scaffold + protocol skeleton.** Four projects, `ImplicitUsings disable`, `Nullable enable`,
   `GlobalUsings.cs` per project. `.gitattributes` → `* text=auto eol=lf`. CI workflow that builds
   and runs unit tests on the three runner images.
2. **JWP envelope + error contract.** `status` codes, empty-body 404, `/status` shape, `ErrorStrings`
   as constants. Protocol tests only — no UIA yet.
3. **CLI + `UsePathBase`.** All four argument forms including `*`. This alone makes existing Appium
   and Selenium Grid configs work.
4. **Session lifecycle.** Create/delete, capabilities, `Root` desktop session, `appTopLevelWindow`.
5. **Window routes, complete.** Including `window_handle`, `window_handles`, `window/size`, and
   every `window/:windowHandle/*` form. Settle window-handle identity — one rule.
6. **Find, with implicit wait.** Correct `tag name` semantics, unknown tag throws, per-request
   timeout override.
7. **Element properties and actions.** Typed returns. Scroll and click policy documented.
8. **Input.** `SendInput` with the correct struct size, `KEYEVENTF_UNICODE` text path.
9. **Mouse and touch routes.** Closes most of the remaining backlog.
10. **Actions payload validation.** 20 backlog tests are error-path only — validation before
    implementation.
11. **Re-measure** against the compat suite; only now is a score meaningful.
12. Then the `#857` / `#1079` work the project exists for.

---

## 7b. Coding conventions

### No `var`

Explicit types everywhere. The type is part of what the line says, and this codebase's whole
problem was call sites whose behaviour you could not read locally.

Two consequences worth stating, because they are the point rather than side effects:

- **No anonymous types.** Every JSON response is a **named record**. This directly removes the old
  implementation's worst inconsistency, where `Results.Json(new { value = new { x, y } })` and
  `Results.Json(new WebDriverResponse<object?>(...))` produced different envelopes from
  code that read the same. One response type per endpoint shape, declared once, serialized once.
- **No JSON round-tripping between layers.** With explicit types there is nothing tempting about
  returning a `string` of JSON from the automation layer and re-parsing it in the route, which is
  exactly what the old `GetCoordinates`/`GetSize` did.

`var` is not used even where the type is obvious from the right-hand side.

### Composition, not inheritance

- **Zero base classes carrying logic.** No `abstract class`, no `protected` members, no template
  methods. If two services share behaviour, one takes the other as a constructor dependency.
- **Interfaces are contracts, not inheritance.** They exist so collaborators can be substituted in
  tests and swapped through DI. Keep them narrow (ISP) — `IElementActions` and
  `IElementProperties`, not one 14-method `IElementInteractor`.
- **`sealed` on every concrete class.** Unsealing is a deliberate decision with a stated reason,
  not a default.
- **No static reachable from a route handler.** Statics are what made the old code untestable.
  `Win32`, condition creation, keyboard input, the UIA factory — all instance types behind
  interfaces, all injected.
- **Test doubles are hand-written stubs** implementing the interface. No shared test base classes
  with logic; where fixtures are needed, compose them in (xUnit class/collection fixtures) rather
  than inheriting from a base.

The one thing SOLID's Liskov rule still buys us here: an interface implementation must be *fully*
substitutable. No partial implementations, no `NotImplementedException` — if a type cannot honour a
contract, the contract is wrong or the type needs a narrower one.

## 7c. Branching

```
main
 └── feat/rewrite-jwp-core          integration branch for the whole rewrite
      ├── feat/rewrite/session-create
      ├── feat/rewrite/window-routes
      └── feat/rewrite/element-find
```

`feat/rewrite-jwp-core` is long-lived and merges to `main` only when it is worth
merging — realistically at parity, or earlier if the old implementation is retired first.

**One sub-branch per complete sub-step.** A sub-step is complete when the build is clean, its
tests are green, and its behaviour has been verified by mutation. Then:

```bash
git checkout feat/rewrite-jwp-core
git merge --no-ff feat/rewrite/<step>
git branch -d feat/rewrite/<step>
```

`--no-ff` so each sub-step stays a visible unit in the history rather than dissolving into a flat
list. The scaffold, protocol contract and session lifecycle landed directly on the integration
branch before this convention was set; everything after uses sub-branches.

Push is separate and deliberate. Local commits accumulate; pushing happens when a unit of work is
finished and stable, or when something specifically needs CI.

## 8. Standards, non-negotiable

From the project's engineering standards, called out because the current code violates all of them:

- Zero warnings. Zero silent `catch { }` — there are currently **34**.
- No repeated literal appearing more than once; the current code repeats one error string 13 times.
- `required` members, no `= null!`, no ambient `!`.
- `ILogger` everywhere; no `Console.WriteLine`. Put window and element rects in session and error
  payloads by default — `012` found that the single highest-value diagnostic.
- No `Thread.Sleep` in production paths. Where polling is unavoidable (window appearance), prefer
  UIA event subscription and document why if polling wins.

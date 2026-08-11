# CLAUDE.md

Guidance for Claude Code working in this repository.

## Read this first

**`docs/PROJECT-KNOWLEDGE.md`** is the single consolidated briefing — protocol
contract, compatibility floor, measured test results, UIA/COM knowledge, and the
mistakes not to repeat. Read it before writing code. `docs/README.md` indexes the
rest.

**Three claims this repository asserted for two weeks were wrong**, and each was
inherited from an earlier session and repeated without checking. See
`docs/FOUNDING-PREMISE.md`. The habit that caused it matters more than the
individual errors: *a claim in this repo is not evidence. Measure it.*

## What this project is

**The WinAppDriver API, implemented on raw `IUIAutomation` COM, built to FlaUI's
standard of capability.**

Three facts define the gap it fills:

- **FlaUI reaches UI Automation properly.** It is pattern-aware, it draws an
  explicit distinction between invoking a pattern and dispatching a real mouse
  click, and it gets at what UIA actually offers.
- **But FlaUI is a .NET library.** It cannot be driven from an Appium suite, from
  Python, or from any existing test that speaks WebDriver. Reaching for it means
  abandoning the protocol.
- **WinAppDriver has the API every existing suite already speaks** — and an
  implementation that is both weak and unmaintained. It is NOT archived — that
  claim was wrong, see docs/FOUNDING-PREMISE.md — but there has been no commit
  since April 2025, 1155 issues are open, and nothing filed
  against it will ever be fixed.

So the answer is not to wrap FlaUI and not to reimplement WinAppDriver's
limitations faithfully. It is to serve WinAppDriver's protocol over a UIA layer
that is as capable as FlaUI's. An existing suite points at it unchanged and stops
hitting the ceiling.

That is why this project depends on `Interop.UIAutomationClient` — FlaUI's own
interop layer, the raw COM surface — and deliberately not on `FlaUI.Core`. It is
a peer, not a wrapper.

**The contract is JSON Wire Protocol, not W3C WebDriver.** Stated outright in
`WinAppDriver/Tests/WebDriverAPI/README.md`. Where they disagree, JWP wins. The
previous implementation was built from the W3C spec and that single wrong choice
produced a large share of its failures.

**Protocol compatibility is the floor, not the ceiling.** Where WinAppDriver's
behaviour is a defect rather than a contract — an unguarded coordinate click, a
lookup that never checks ancestors — match the protocol and fix the behaviour.
Where a capability has no expression in the protocol, add a vendor extension
(`windows: invoke` versus `windows: mouseClick`) rather than silently choosing
for the caller.

## Current state

A rewrite is in progress on `feat/rewrite-jwp-core`. The original implementation
still sits in `WindowsDriverCore/` as reference and will be deleted at parity.

```
src/WindowsDriverCore.Host          composition root, CLI, DI
src/WindowsDriverCore.Protocol      JWP surface — routes, envelopes, faults. No UIA.
src/WindowsDriverCore.Automation    element find. Typed in, typed out. No HTTP.
src/WindowsDriverCore.Platform      Win32, window discovery, process lifetime
tests/…Unit  …Protocol  …Integration
bench/…Benchmarks  …Fuzz
```

Working: `/status`, unknown-command fallback, session create/list/delete,
`/orientation`, element find (`/element`, `/elements`), CLI argument forms and
base path, classic and packaged application launch.

Not working yet: element interaction, window routes, mouse/touch/Actions,
implicit wait, XPath, screenshots. `docs/LIMITATIONS.md` is the live list.

## Claims

The goal above is the claim. Two footnotes keep it honest:

- **Do not claim it fixes issue #857.** The elements are absent from the UIA tree
  entirely and Inspect.exe cannot see them either, so no client can. See
  `docs/FOUNDING-PREMISE.md` — the bug numbers this repo was founded on were both
  misdescribed, and they were never the real justification anyway.
- **The capability claim is the one with evidence.** `docs/CLICK-SEMANTICS.md`
  has a documented cause, a reproduction, and a measured before/after from a real
  application suite. Prefer it to anything about issue numbers.

**Measured 2026-08-11 on the rebuilt offline guest** — Windows 10 19045, Alarms &
Clock `10.1906.2182.0`, no network, static 4 GB, cold, **no alarm-store reset and
no warm step**:

| | score |
|---|---|
| WinAppDriver 1.2.1 | **280/290** |
| environmental failures | 9 (3 ModernApp, 5 browser/EdgeBase, 1 `GetLocation`) |
| **reachable ceiling** | **281** |
| **this driver** | **203/290** |
| **the gap** | **77 tests** |

**Every earlier figure in this file was wrong, including ones measured
repeatedly.** Two pieces of scaffolding introduced on 2026-08-10 in a single
commit (`c62ebde`) — an alarm-store reset and an app-warm step — turned out to be
perturbing the instrument:

```
reset + warm      281      the pair roughly cancelled
reset, no warm    259/259/260   21 ActionsError failures
no reset, no warm 280       0 ActionsError failures
```

The reset moves the app's `Settings` folder away, forcing a genuine cold start,
and a cold activation returns an intermediate process id — so both drivers hunt
for a window owned by a process that does not own it. The warm step hid that.
Removing the warm exposed it; removing the reset fixed it.

**Three careful runs agreed on 259 and all three were wrong**, because they shared
the confound. Reproducibility is not validity when the thing being reproduced is
the confound.

Quote a score only with: the Windows build, the **Alarms & Clock version**,
whether the store was reset, and whether the app was warm. `169`, `231`, `163`
and `259` all appear in this repository's history and none of them are comparable
to each other. See `docs/LIMITATIONS.md`.

A find takes roughly 33 ms here against roughly 1070 ms through WinAppDriver,
under unmatched conditions.

## As close to the metal as possible — meaning fast

Standing direction, and it is a performance goal first. Every layer between this
code and UIA costs time, and the whole reason to write a driver rather than use
one is that the layers WinAppDriver has are not paying for themselves.

**The targets, in order:**

| Subject | Role | Measured |
|---|---|---|
| FlaUI, in-process | **the floor** — what this costs with no transport at all | not yet |
| This driver | floor + HTTP hop + our layering | ~33 ms per find |
| WinAppDriver | **the baseline to beat** | ~1070 ms per find |

Roughly 30x on the one measurement taken, under unmatched conditions. The gap
between us and FlaUI is the optimization budget; closing on FlaUI is the goal,
and it is what `bench/WindowsDriverCore.Benchmarks` exists to track.

**What that implies, concretely:**

- **Round trips dominate**, not codegen. This is cross-process COM. If a find
  shows up in a benchmark the answer is `IUIAutomationCacheRequest` — fetch more
  per trip — or holding the element the caller already named.

  **Corrected 2026-08-08, and this rule previously said the opposite.** It read
  "never a snapshot held *between* calls, which trades correctness for speed".
  `HeldElementLivenessTests` measured the premise: an `IUIAutomationElement`
  obtained without a cache request is a **live proxy, not a snapshot**. Every
  `Current*` read crosses to the provider, its runtime id survives changes to the
  tree, and once the element is destroyed it throws `UIA_E_ELEMENTNOTAVAILABLE`
  rather than answering with the last value it saw.

  So the ban never applied to element handles. What it correctly forbids is
  holding a *cached property snapshot* (`FindAllBuildCache` results reused across
  calls) or a stale *result set*. Holding a live handle to an element the client
  has already been given an id for is neither.
- **Raw COM interfaces, no managed wrappers.** `IUIAutomation` directly; no
  `System.Windows.Automation`, no `FlaUI.Core`. Each wrapper is a layer whose
  cost and behaviour you inherit and then have to explain.
- **Deterministic COM lifetime.** `ComScope`, explicit `ReleaseComObject`, not
  finalizer roulette. The previous implementation stored elements and released
  none of them.
- **Source-generated P/Invoke** (`LibraryImport`) where it marshals, `DllImport`
  where it will not, with the reason at the declaration. `unsafe` is on in
  `Platform` because that is what the generator emits.
- **No hidden behaviour** — no implicit retries, no caching the caller did not
  ask for, no exception translation that loses the original. This one is not a
  speed argument, but it is what keeps the speed honest: a driver that quietly
  caches *values* looks fast and is wrong. Keeping a live handle against the
  element id the caller supplied is not that — the id **is** the caller asking
  for that element, and every property still comes from the provider.

## Rules that are not negotiable

- **Measure, do not infer.** Wire behaviour comes from
  `tests/WindowsDriverCore.Tests.Protocol/Recordings/winappdriver-responses.json`,
  captured from the real server. Do not hand-edit it; re-record it.
- **Test-first.** A test that has never been red proves nothing. Where that is
  not possible, verify by mutation — and make the mutation assert it applied and
  compile cleanly, or a build failure will masquerade as an uncaught mutation.
- **No `var`.** Explicit types everywhere, which also rules out anonymous types
  and forces every response to be a named record.
- **Composition, not inheritance.** Interfaces are contracts for substitution;
  no base class carries logic; `sealed` on every concrete class.
- **No static reachable from a route handler.** That is what made the previous
  implementation untestable.
- **The automation layer does not know HTTP or JSON exist.** Enforced by
  `Automation` and `Platform` not referencing ASP.NET Core.
- **Zero warnings.** `TreatWarningsAsErrors`, `AnalysisModeSecurity=All`.

## Branching

`feat/rewrite-jwp-core` is the integration branch. One sub-branch per complete
sub-step, merged back with `--no-ff`, branch deleted. A sub-step is complete when
the build is clean, its tests are green, and its behaviour is mutation-verified.

## Commands

```powershell
# Build and test
dotnet build WindowsDriverCore.slnx
dotnet test WindowsDriverCore.slnx

# Skip the slow WinAppDriver comparison (6+ minutes)
dotnet test WindowsDriverCore.slnx --filter "TestCategory!=Comparison"

# Run the server
dotnet run --project src/WindowsDriverCore.Host

# The request transcript. Console by default, as WinAppDriver's is; set the
# variable to send it to a file instead. An environment variable rather than a
# switch, because the argument grammar is a compatibility contract.
$env:WINDOWSDRIVERCORE_LOG = "C:\temp\driver.log"

# Requests at the margin, the work they caused indented under them:
#
#   ...36.030Z   launch 'Microsoft.WindowsCalculator...' -> pid 34112 window 0x2A204C6 760.7 ms
#   ...36.038Z POST /session -> 200 jwp 0 776.9 ms
#   ...36.121Z   find AutomationId='num5Button' -> 1 match(es) 55.4 ms
#   ...36.123Z POST /session/{id}/element -> 200 jwp 0 79.0 ms
#   ...36.183Z   Click -> Performed via Invoke 49.2 ms
#   ...36.226Z   find AutomationId='NormalOutput' -> 0 match(es) 35.5 ms
#   ...36.234Z POST /session/{id}/element -> 404 jwp 7 44.2 ms
#
# Read three things off that: the find cost 55.4 of the request's 79.0 ms, so
# 23.6 ms is our own overhead; the click went via Invoke rather than an ancestor
# climb or the mouse; and NormalOutput RAN and matched nothing, which is a fact
# about the application, where a search that could not run reads "FAILED: ...".
#
# Locators are logged. SetValue and SendKeys arguments never are — that is where
# a password appears, and IInteractionLog has no parameter that could take one.

# WinAppDriver-compatible argument forms
WindowsDriverCore.exe                       # 127.0.0.1:4723
WindowsDriverCore.exe 4727                  # port only
WindowsDriverCore.exe 10.0.0.10 4725        # host and port
WindowsDriverCore.exe 10.0.0.10 4723/wd/hub # base path rides on the PORT argument
WindowsDriverCore.exe * 4723                # all interfaces

# Mutation testing (reports; break threshold is 0 until it earns raising)
dotnet stryker

# Compatibility suite against a running server — the scoreboard, kept UNMODIFIED
& "F:\VisualStudio2026\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" `
  "C:\Users\pinku\source\repos\PinKushin\WinAppDriver\Tests\WebDriverAPI\bin\Debug\WebDriverAPI.dll"

# Clean up orphaned test apps
Get-Process CalculatorApp,Notepad,WinAppDriver -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Ground truth worth memorising

- **280/290 for WinAppDriver 1.2.1 on the rebuilt guest**, cold, no reset, no
  warm — Windows 10 19045, Alarms & Clock `10.1906.2182.0`, offline, static 4 GB.
  Nine of the ten failures are environmental, so the reachable ceiling is **281**.
  **This driver scores 203/290 under the same conditions — a gap of 77.** Every
  earlier figure for either driver was taken through scaffolding that moved the
  result.
- **The backlog, read from a request transcript rather than a test list:** 80
  finds answering `no such element`, 18 `POST /actions` rejecting arguments with
  status 100, 10 session creations failing on absent Edge. Removing the reset and
  refusing to return a dead window took our failing requests from 294 to 203, and
  `POST /element` answering `no such window` from **105 to 1** — a prediction
  stated in `044b71c` before the run that tested it.
- **The alarm-store reset costs 21 tests, and the warm step hid that.** Both were
  added in one commit (`c62ebde`, 2026-08-10) to stabilise the score:

  ```
  reset + warm       281            the two roughly cancelled
  reset, no warm     259/259/260    21 ActionsError failures
  no reset, no warm  280             0 ActionsError failures
  ```

  The reset moves the app's `Settings` folder away, forcing a real cold start; a
  cold activation returns an **intermediate process id**, and both drivers then
  search for a window owned by a process that does not own it. Caught directly in
  our transcript: activation returned pid 3024 while Alarms ran as **4852**, and a
  second activation a second later returned 4852 in 790 ms. Resetting is now
  opt-in (`-ResetStore`).
- **Three careful runs agreed on 259 and all three were wrong.** Load varied
  62/40/54%, memory dynamic versus pinned — and every run shared the confound.
  Reproducibility is not validity when what is being reproduced is the confound.
- **A wait that spends its whole budget never detected the thing.** Nothing this
  suite drives legitimately takes ten seconds, so a launch reporting ~10,000 ms is
  a search looking for the wrong process — never a slow application. The
  transcript now prints `TIMED OUT` rather than a number a reader must recognise.
- **`settings.dat` size is not an alarm count.** It is a registry hive: it
  allocates in blocks and never shrinks. Reported as a leftover-work metric for
  half a day before the owner looked in the app and found the alarms gone —
  meaning the suite's cleanup, including the right-click and the context-menu
  find, works against this driver.
- WinAppDriver scored **112/290 on Windows 11**, which is app drift and must never
  be quoted as a capability number.
- A find takes roughly **33 ms** here against roughly **1070 ms** through
  WinAppDriver — unmatched conditions, but a 30x gap.
- Compatibility floor is **Windows 10 1607 / Server 2016**, the same as
  WinAppDriver's. .NET reaches further back than that, so the framework is not
  the constraint.
- The compatibility suite lives in the sibling `WinAppDriver/` repo and is kept
  **pristine**. Local modifications are stashed there, not committed.

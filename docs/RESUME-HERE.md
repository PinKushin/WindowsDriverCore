# Resume here — updated 2026-08-30

The single entry point after a break. Read this, then `docs/LIMITATIONS.md` for
the detail behind any line of it.

---

## 0. Where it stands, 2026-08-30

**Last measured: 273–275/290, backlog 9 named tests** (`b480f52`, four runs
under identical conditions: 275, 275, 273, 274). Reference 281, ceiling 281.

**Quote the range.** Five tests fail in every run and four flap, and the
flappers count:

| stable (4 of 4) | intermittent | runs failed |
|---|---|---|
| `ClickElement` | `Touch_Scroll_Vertical` | 3 of 4 |
| `MiscellaneousSessionError_StaleSessionId` | `Pen_Scroll_Vertical` | 2 of 4 |
| `NavigateBack_SystemApp` | `FindElements_ByName` | 1 of 4 |
| `TouchDoubleTap` | `NavigateBack_ModernApp` | 1 of 4 |
| `TouchLongTap` | | |

**Three commits after `b480f52` are unmeasured** and a run was in flight when
this was written: the empty-segment collapse plus `GET /session/{sessionId}`
(aimed at `MiscellaneousSessionError_StaleSessionId`), navigation recording its
own dispatched gesture, and the menu rung falling through rather than refusing.

**`MouseDoubleClick` and `MouseDownMoveUp` were ONE defect and are fixed.** The
system menu `MouseClick` opens survives an `InvokePattern` click, because an
Invoke sends no input and only real input ends a modal menu loop. They had been
counted as separate items for weeks — `MouseDownMoveUp` fails on line 137 rather
than 136, so the window really did move, which is what a menu item being
*activated* between the down point and the up point looks like. See
DECISIONS §9.

**Two probes are written and not yet run**, both aimed at the stable five:
`tools/vm/probe-how-does-back-finish.ps1` samples Explorer's title on a schedule
after `/back` to tell a race apart from a gesture that never landed, and
`tools/vm/probe-what-gap-makes-a-double-tap.ps1` sweeps the separation between
two injected taps, since `/touch/doubleclick` currently sends them back to back.

---

## 0b. What changed on 2026-08-29, and what it means

**Measured then: 268/290, backlog 13** (`b207b92`) — superseded by the figures
above.

**Three protocol gaps found by audit, none of which the suite can see.**
`/touch/flick` ignored the spec's `speed`; `element/{id}/value` read only the
JSON Wire array so a Selenium 4 client typed nothing and got a 200;
`POST /window` read only `name` so a W3C client could not switch windows. See
`docs/PROTOCOL-AUDIT.md` — the audit is a standing sweep now, and by its own
standard it stands at **zero of three clean passes**.

**The scroll family is understood.** A `LoopingSelector` flings on VELOCITY, and
`/touch/scroll` used a fixed 300 ms whatever the distance — 55 px over 300 ms is
183 px/s, below the fling threshold, measured 0/3 against the reference's 3/3.
Now paced by speed. The two `/actions` scroll tests carry a client-stated
duration and are NOT fixed by this.

**Three mechanisms for the scroll family died before that one:** the overrun
theory (refuted — frames fit their slot at 1.0x), dirty start state (refuted —
two runs of identical code after full runs still differed by three tests), and
the gesture-threshold theory (refuted — the gesture is deterministic in
isolation, 6/6). The fourth was measured rather than reasoned.

---

## 1. The first thing to do, before writing any code

**Commits are unmeasured.** The last full guest run was `b207b92` at
**268/290**. Since then, and never scored:

```
758f9ec  flick speed + W3C request shapes   expect: nothing in the suite -
                                            it is a Selenium 3 client and
                                            cannot send these
834cd17  scroll paced by SPEED              expect: TouchScrollOnElement_Vertical
```

Earlier commits now confirmed by the `b207b92` run: the desktop-title fix landed
(`CreateSession_Desktop`, `GetTitle_Desktop` both recovered), and the mouse-drag
path did NOT fix `MouseDownMoveUp`, which is still failing and still unexplained.

So the first action is one full run and one comparison:

```powershell
# from the repo root, with the VM started
git bundle create "$env:TEMP\WindowsDriverCore.bundle" --all
Copy-VMFile -Name Win10-Baseline -SourcePath "$env:TEMP\WindowsDriverCore.bundle" `
  -DestinationPath "C:\baseline\payload\WindowsDriverCore.bundle" -FileSource Host -CreateFullPath -Force
pwsh ./tools/vm/Invoke-CompatibilitySuite.ps1 -Commit (git rev-parse --short HEAD)

pwsh ./tools/vm/Compare-Runs.ps1 -Before bfa5638 -After <sha> -Reference 044b71c8
```

**Do not skip the comparison and read the score.** A score cannot tell you which
tests moved, and three of the sixteen backlog tests are intermittent enough to
swamp a two-test gain.

---

## 2. Where the numbers actually are

| | score | commit |
|---|---|---|
| WinAppDriver 1.2.1 (the reference) | **281/290** | `044b71c8` |
| this driver, last measured | **268/290** | `b207b92` |
| this driver, best measured | **269/290** | `8dfd26f` |
| **backlog** | **13 named tests** | |

`268 + 13 = 281` closes exactly, which is the check that two runs are
comparable. `Compare-Runs.ps1` prints it and says so when it does not close.

Conditions for every number above: Windows 10 19045 guest, Alarms & Clock
`10.1906.2182.0`, offline, static 4 GB, cold, **no store reset, no warm step**.
A score quoted without those is not comparable to these.

### The 13, and what is known about each

Three entries below are now resolved and kept for the record:
`CreateSession_Desktop` and `GetTitle_Desktop` PASSED at `b207b92`, and
`TouchScrollOnElement_Vertical` has a measured cause and a fix awaiting a run.

| test | what is known |
|---|---|
| `MouseClick` | **Not a broken mouse path** — measured working on the host, repeatedly, including on an immediate read. Fails on the guest at the FIRST assertion. See §4. |
| `ClickElement` | Same family: read beats the app. Reference takes 8.17 s, we take 0.29 s. |
| `GetElementDisplayedState` | Same family. Reference 9.64 s, we take 1.51 s. |
| `MouseDoubleClick` | Same family. Reference 4.26 s, we take 1.03 s. |
| `MouseDownMoveUp` | **Cause found and fixed** in `4527b0d`, unmeasured. `MoveTo` sent one absolute jump; a drag needs a path. |
| `TouchDoubleTap` | Same family as the clicks. Passed once in an isolated filtered run after the PRIMARY flag; not confirmed in a full run. |
| `TouchLongTap` | **Never passed in any of 9 runs.** See §5 — this is the interesting one. |
| `Pen_Scroll_Vertical` | `/actions`, 200 px over a CLIENT-stated 500 ms = 400 px/s, near the fling boundary. Not fixed by the speed change — the duration is the client's. |
| `Touch_Scroll_Vertical` | Same. The three scroll tests are ANTI-CORRELATED: when one passes the others fail, because they share one session and one selector and nothing resets its scroll position. |
| `TouchScrollOnElement_Vertical` | **Cause found and fixed** in `834cd17`, unmeasured. `/touch/scroll` ran at 183 px/s, below the fling threshold; measured 0/3 against the reference's 3/3. |
| `NavigateBack_ModernApp` | Fails 7 of 9. Cause understood, fix did not work. See §6. |
| `NavigateBack_SystemApp` | Not investigated. File Explorer back-navigation; title stayed "Temp" instead of "File Explorer". |
| ~~`CreateSession_Desktop`~~ | **PASSES** as of `b207b92`. The desktop has no Win32 caption; its title comes from UIA. |
| ~~`GetTitle_Desktop`~~ | Same fix, same run. |
| `FindNestedElement_ByRuntimeId` | Not fixed. See §7. |
| `MiscellaneousSessionError_StaleSessionId` | Not fixed. See §8. |

### The 9 that are NOT backlog

`CreateSessionWithArguments_ModernApp`, `GetLocation`,
`GetWindowHandles_ModernApp`, `NavigateBack_Browser`, `NavigateForward_Browser`,
`SwitchWindows`, `TouchFlick_Arbitrary`, `TouchSingleTap`,
`TouchSingleTapError_StaleElement`.

**WinAppDriver fails these too** — no Edge installed, no Store apps. Reading the
failing list as a to-do list spends real work on tests the reference cannot pass
either. `TouchSingleTap` is the trap: it reads as an obvious capability gap and
is not ours.

---

## 3. Flake, and what the bar actually is

The owner's standard: *"we dont have parity if we have flake, winappdriver
doesnt have flake"*, and *"flake is failure you know"*. Recorded as
`docs/DECISIONS.md` #3 and #4.

**The reference is not literally zero, and the real figure is the useful one.**
Across five WinAppDriver runs on this guest, exactly **three** tests ever changed
verdict:

```
SendKeys_ModifierAlt                        pass pass FAIL
SendKeys_ModifierWindowsKey                 FAIL pass pass
CompareElementsError_StaleElementParameter  pass FAIL
```

About one per run. **Ours is 13.** That gap — deterministic failures versus
shifting ones — is the parity gap as much as the score is.

### The census, and the thing it corrected

Across nine of our runs (excluding `a8f9208`, where one defect cascaded into 14
tests and would show as mass flake it did not cause), **18 tests move**. But:

- **Five are not flake at all** — `GetElementSize`,
  `GetOrientationError_NoSuchWindow`, both `*_OriginPointer`,
  `Pen_Click_BarrelButton` — they were broken and got fixed mid-window.
- **The 8-of-9 group is TWO EVENTS, not eight flakes.** In `f273d0d`,
  `Pen_LongClick` and all four `SendKeys_*` failed together. In `67f5dc3e`,
  `FindElements_ByName`, `TouchDownMoveUp_DragAndDrop`, `Touch_Flick` and
  `ActionsError_NoSuchWindow` failed together.

**A failure that always co-occurs is one defect.** Counting the column instead
of the row inflates the flake list by a factor of four and hides that there are
really two unexplained upsets, each worth one investigation.

The genuinely persistent ones, ranked:

```
7/9 fail   NavigateBack_ModernApp
6/9 fail   TouchScrollOnElement_Vertical      <- biggest unexamined flake
3/9 fail   FindElements_ByName
2/9 fail   Pen_Scroll_Vertical, TouchDownMoveUp_DragAndDrop
1/9 fail   Touch_Scroll_Vertical
```

---

## 4. The finding that reframes a third of the backlog

**MEASURED**, per-test durations from the TRX, same guest:

```
                             WinAppDriver   this driver
  MouseClick                       3.90 s       0.067 s   fails
  ClickElement                     8.17 s       0.29 s    fails
  GetElementDisplayedState         9.64 s       1.51 s    fails
  MouseDoubleClick                 4.26 s       1.03 s    fails
  TouchDoubleTap                   3.98 s       1.09 s    fails
  TouchLongTap                    14.81 s       8.32 s    fails
```

**We fail these by being 10–60x faster than the reference.** The tests carry no
synchronisation of their own — they click and then read. WinAppDriver passes
because a single find costs it ~1070 ms, so the application has caught up by
accident.

This is not an argument for being slower. It is *act early but wait as long*:
succeed as soon as the condition holds, give up only after the reference would.
Answering before the application has reacted is reporting the **wrong state**,
not reporting it quickly.

**What was done about it** (`83a846e`, unmeasured):

- `InputPending` became a timestamp (`DispatchedAt`), so a dependent read can
  wait out a floor measured from the DISPATCH — a client that does other work in
  between pays nothing.
- A **120 ms reaction floor** before a property read that follows input.
- **`/touch/*` and `/actions` never marked input dispatched at all.** Only
  keyboard, mouse and element-action routes did, so a read after a tap or drag
  had never waited for anything since those routes were written.

**The floor's value is NOT yet justified by measurement.** A sweep at 0 / 120 /
300 ms against the mouse tests showed no difference — because `MouseClick`
*passes* in an isolated filtered run at every value, so the manipulation had no
failing subject to act on. That sweep is invalid for its purpose, not evidence
the floor is useless. It has to be judged in a full run.

`WDC_REACTION_MS` is left readable so the sweep can be repeated. It is a sweep
hook; do not treat 120 as measured.

---

## 5. TouchLongTap — the most interesting unsolved one

**It has never passed, in any of nine runs.** Not a regression.

The decisive observation, from two moments in the *same* test seconds apart on
the *same* element:

```
02:50:35.471  click button 2 -> (616,324)          <- mouse right-click
02:50:38.489  find Name='Delete' -> 1 match(es)    <- context menu appeared

02:50:48.313  POST /touch/longclick -> 200 (1099 ms)
02:50:51.344  find Name='Delete' -> 0 match(es)    <- no menu; 38 retries, 404
```

A mouse right-click opens the context menu. A 1.1-second held touch contact at
the same point does not. The test then fails on the FIND, several steps
downstream of the cause — which is why it reads as "an element could not be
located" and was mis-triaged twice.

**Tried and did NOT fix it:** `POINTER_FLAG_PRIMARY` on all three phases, in
both injection paths (`bfa5638`). Windows only promotes the primary pointer to
gestures, so this was a real and necessary gap — it recovered `Pen_LongClick` —
but the long press still produces no menu.

**Not yet tried, roughly in order of promise:**

1. **The window is never brought to the foreground.** `/touch/*` does not raise,
   unlike the navigation and keyboard routes. A contact into a non-foreground
   window may be consumed by activation.
2. **WinRT vs Win32 initialisation differs.** The WinRT injector uses
   `InjectedInputVisualizationMode.Default`; the Win32 path sets
   `TOUCH_FEEDBACK_INDIRECT`. Probably only visuals, but it is an unexplained
   asymmetry between two paths that are supposed to be equivalent.
3. **Ask the reference.** What does WinAppDriver actually emit for
   `/touch/longclick`? If it issues a right-click rather than a held contact,
   that is worth knowing before spending more on faithful touch. It takes 14.8 s
   there, which is a long time and may be doing something else entirely.

**Do not substitute a mouse right-click without deciding that deliberately.**
The owner has previously called that class of shortcut "a cheat", and our touch
hold *should* raise a menu on Windows — if it does not, something about our
injection is wrong and worth finding.

---

## 6. NavigateBack_ModernApp — cause understood, fix failed

**The application EXITS during the second `/back`.** Nothing of ours kills it:
the only `terminate` for that pid is two seconds later, and the window was alive
at the preceding find, which ran and returned zero rather than faulting.

Diffing a passing run against a failing one, same test:

```
PASSED   23.825 POST /back → 23.865 find SecondaryButton -> 1 match (discard dialog)
                → suite dismisses it → back on the list
FAILED   36.579 POST /back → 37.223 find SecondaryButton -> 0 match
                → 37.279 NoSuchWindow, the app is gone
```

The only upstream difference is **speed**. Time from the AddAlarm click to
finding `EditAlarmHeader`: **122 ms passing, 66 ms failing.** We reach the edit
page twice as fast and send Alt+Left before it has set up its navigation state.

**The fix that did not work** (`352357a`): make `/back` wait for previously
dispatched input. The wait ran and returned in 0.4 ms — `InputPending` was
correctly true, and `WaitForInputIdle` reported the app **idle, because it was**.
It answers "is this process waiting for input", not "has this page finished
becoming itself".

Kept anyway: it enforces a correct ordering and costs nothing when idle. But it
is not the fix, and the docs say so.

**The next candidate is upstream of all of it.** The FIRST `/back` fires **2 ms**
after `POST /session` returns, into an app with no content — `find AlarmButton`
then misses seven times over 600 ms. `ContentReadyLauncher` answered in 25.8 ms
because its readiness condition is *"some descendant has a non-empty
AutomationId"*, and an `ApplicationFrameWindow` satisfies that **with its own
chrome** (TitleBar, AppName, the caption buttons) before the CoreWindow holds
anything. That is a measured weakness in the condition and is worth fixing on
its own merits, independent of this test.

---

## 7. FindNestedElement_ByRuntimeId — a hypothesis, not a finding

The test searches `alarmTabElement` for `AddAlarmButton`'s element id.
`alarmTabElement` is the alarm **tab** (a nav list item); `AddAlarmButton` is in
the page content and is almost certainly **not** a descendant of it. Yet
WinAppDriver passes.

**HYPOTHESIS:** WinAppDriver's `FindElementById` is a lookup in its own element
cache, so the scoping container is ignored. We use the UIA runtime id as the
element id and genuinely scope the search — correct by the letter, different
from the reference.

**Settle it by asking the reference**, not by changing our behaviour: nested-find
an element known to be outside the container and see whether WinAppDriver
returns it. Ten minutes on the guest.

---

## 8. MiscellaneousSessionError_StaleSessionId — needs a recording

The suite asserts the message **starts with** `"No active session with ID title"`
after `session.Quit()` then `session.Title`. We emit
`"No active session with ID {the actual guid}"`.

The recorded wire capture in
`tests/…/Recordings/winappdriver-responses.json` covers
`GET /session/{id}/window_handle` and shows the reference using the **real
session id** there. It does **not** cover `/title` on a dead session, which is
exactly the gap.

**So re-record that one interaction against the real WinAppDriver before
changing anything.** Do not hand-edit the recording — the rule is in `CLAUDE.md`
and this is precisely the case it exists for. It is possible the reference
interpolates the command name rather than the id for this route, which would be
a defect of theirs the suite has encoded; that is worth knowing before deciding
whether to match it.

---

## 9. Tooling added this session — use it

**`tools/vm/Compare-Runs.ps1`** — diffs two runs by test NAME, separates the
reference's own failures from the backlog, and prints the closing arithmetic.
**Selects the full run by RESULT COUNT (290), never by filename**, and refuses
anything else with the candidate sizes listed.

Why it exists: an ad-hoc comparison reported **287/290** — above the
reference — by picking a *filtered* run's 6-result TRX with `Select -First 1` on
a name prefix. Twenty tests appeared to recover; they were absent. Two of them
fail because Edge is not installed, which no code change can fix, and that
impossibility is what exposed it. **A score that beats the reference is a red
flag, not a result.**

**`Invoke-CompatibilitySuite.ps1 -TestCaseFilter`** — a full run is ~25 minutes,
which is why this project keeps reasoning its way to wrong answers instead of
measuring. A filtered run is well under a minute.

**`-DriverEnvironment "NAME=VALUE"`** — sweep a constant without a rebuild per
value. It echoes what it sets, because a sweep whose value never reached the
driver looks identical to a value that made no difference.

Two hazards, both documented at the parameters and both hit for real:

- A filter matching nothing **exits 0 with no summary**, so the caller reads
  success.
- **A filtered run is not a smaller suite.** Fixtures share one session and
  inherit each other's app state. Use it to compare a subset against ITSELF
  across a manipulation; never to claim a test would pass in a full run, and
  never to quote a score.

---

## 10. Method notes worth more than any single fix

**Read the failure MESSAGE, not the test name.** Doing this for all 16 at once
corrected two of my own triages in a single sitting: `Pen_Click_BarrelButton`
was not a missing click flag (it is an element lookup, downstream of a context
menu that never opened), and `MouseClick` fails on its first assertion, nothing
to do with the desktop session it uses twenty lines later. Four tests shared one
symptom and turned out to have three different causes.

**Put the measurement in the fault.** `(101,33) is outside the application
window` is unfalsifiable — it says a point was rejected and nothing about what
it was rejected against. Two investigations ran off it, one of which cost a
guest run and three tests. Adding the rectangle solved it in one subtraction:
`(115,87) is outside the application window at (208,87) 816x641` → offset −93
with zero Y → that is the test's *second* gesture → the pointer position was not
surviving between requests.

**A mutation must compile.** Twice in one session a mutation removed a
constant's only use, the build failed, and a build failure looks exactly like an
uncaught mutant. Mutate a VALUE, not a use.

**A test that passes on first write has proven nothing.** The reaction-floor
timing test passed with the floor set to **zero** — it was measuring
first-request routing and JIT warm-up, not the floor. Warm the route, then time.

**Diff names before theorising about a count.** And check whether a "regression"
has ever flapped: `Pen_Scroll_Vertical` failed after 13 consecutive passes,
which looked like clean attribution, and recovered on the next run at a commit
with no related change.

---

## 11. Housekeeping state at the pause

- Working tree clean, everything pushed, HEAD `fb8578c`.
- **VM `Win10-Baseline` is Off.** Start it before any guest run.
- Host test processes killed.
- Local suites at HEAD: unit **162**, protocol **287**, integration **194/199**
  (5 skipped), zero warnings.
- CI was green with zero annotations at last check.

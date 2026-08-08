# 009 — Test Progress and Known Failure Categories

## Date: 2026-08-02

## The control run — what the denominator actually is (2026-08-08)

Ran the identical suite binary against **real WinAppDriver 1.2.2009.02003** on Win11 26200, to
establish what is achievable at all. 28.6 minutes, 290 tests.

| | Passed | Failed |
|---|---|---|
| **WinAppDriver (control)** | **112** | 178 |
| Ours (test-results-v2, older build) | 77 | 213 |

Cross-tabulated:

| WinAppDriver | Ours | Count | Meaning |
|---|---|---|---|
| Passed | Failed | **70** | our real bug backlog |
| Passed | Passed | 42 | already banked |
| Failed | Failed | 143 | unmeasurable — see the poisoning section below |
| Failed | Passed | 35 | artifact of our per-test relaunch bug, not wins |

**"80 / 290 = 27.6%" was never a meaningful number.** WinAppDriver itself scores 112. And 112 is
not the true ceiling either — it is WinAppDriver-with-a-poisoned-fixture. Both figures are
measurements of a broken harness.

Caveat on precision: our 77 comes from `test-results-v2.txt`, produced before the current test DLL
was rebuilt (results 13:51, DLL 14:11 on 2026-08-02). The bucket *structure* is robust; the exact
counts need a re-run of our server against this same binary.

### The 70-test backlog, grouped

- **20** — `ActionsError_*`. Pure payload validation, no Actions implementation required.
- **13** — window size/position/maximize. Maps exactly onto the missing JWP routes
  (`/window/size`, `/window/:windowHandle/{size,position,maximize}`).
- **4** — `/touch/*`; **3** — mouse (`/click`, `/doubleclick`, `/buttondown`, `/buttonup`, `/moveto`).
- **8** — element location / location_in_view / size.
- **4** — `GetActiveElement`; **3** — `CompareElements` (`/element/:id/equals`).
- **4** — session creation and lifetime; **2** — navigation; **2** — orientation.
- remainder — assorted stale-element error paths.

Roughly 40 of the 70 are missing JWP routes plus request-payload validation. Both mechanical.

## Historical score (superseded, kept for context)

80 / 290 passed (27.6%). Previous score: 77 / 290.

## What Changed This Session
- `/rect` endpoint: Unblocked all location/size tests that use W3C protocol
- `DualGetPost`: Unblocked element property tests that old client sends as POST
- COM exception handling: Prevents raw COM errors from leaking to clients
- `AllowStatusCode404Response`: Prevents exception handler from crashing on stale element errors

## Failure Categories

### 1. AlarmClockBase state poisoning (167 tests — the single largest block)

**CORRECTED TWICE. Read the whole section; the first correction was also wrong.**

**The actual mechanism (established 2026-08-08 by running the suite against real WinAppDriver
1.2.2009.02003 — see the control-run section at the top of this file):** the Alarms fixture works
at the start of a run and is then **poisoned partway through**, after which every remaining test
inherits the broken state.

Execution-order evidence from the control run:

```
 1–18  Passed   ActionsError_*             (40–150 ms)
 19    Failed   ActionsError_StaleElement  (3 s)     ← GetStaleElement()
 26–27 Passed   Pen_DoubleClick / DragAndDrop
 28+   Failed   everything                 (10 s each — full implicit wait)
```

From test 28 onward every failure is identical:
`TestInit` → `DismissAddAlarmPage()` → `AlarmClockBase.cs:175` → cannot find `Back`.

`GetStaleElement()` clicks `AddAlarmButton` to open the Add Alarm page, then
`DismissAddAlarmPage()` tries `CancelButton` and falls back to `Back`. On Win11 both fail, so the
app is left stranded on the Add Alarm page.

### What the experiment settled (run 2026-08-08 against WinAppDriver, raw JWP over HTTP)

Two hypotheses were proposed and **both are dead**. Recorded so nobody re-derives them:

1. ~~The `protected static session` field carries the poison.~~ **False.** All 20 classes deriving
   from `AlarmClockBase` have `[ClassCleanup] → TearDown() → session.Quit(); session = null`.
2. ~~Alarms is single-instance, its process survives `Quit()`, and a new session re-attaches to the
   stranded window.~~ **False, measured:**

```
STEP 4  after clicking AddAlarmButton:
        AlarmButton   found = True      <- still reachable from the Add Alarm page
        CancelButton  found = False
        Back          found = False
STEP 5  DELETE /session -> HTTP 200;  Alarms process: NONE   <- app DOES terminate
STEP 6  new session -> Alarms PID 3021448 (fresh process)
STEP 7  AlarmButton found = True        <- fully recovered
```

Nothing survives session teardown. The app closes and comes back clean.

### What the experiment *did* find — genuine Win11 drift

**The Win11 Add Alarm page exposes neither `CancelButton` nor `Back`.** `DismissAddAlarmPage()`
tries `CancelButton`, falls back to `Back`, and on this OS **both are gone** — so that method can
never succeed. Whenever `TestInit` falls into its `catch`, it throws from `:175` and the test fails.
That is real app drift and it is fatal every time it is reached.

### RESOLVED — it is stale automation IDs, nothing more

Driven end to end through WinAppDriver against the live app:

```
STEP 1  WinAppDriver clicks AddAlarmButton  -> dialog OPEN?        True
STEP 2  WinAppDriver clicks CloseButton     -> dialog STILL OPEN?  False
```

**WinAppDriver opens and closes the Add Alarm dialog correctly.** There is no click-semantics
problem, no `#857` tree-visibility problem, and no window-rooting problem. Everything works when
the correct automation id is used.

The Win11 dialog renamed its controls:

| `AlarmClockBase` expects | Win11 actual | Notes |
|---|---|---|
| `CancelButton` | **`CloseButton`** | name is still `Cancel` |
| `Back` (fallback) | *gone* | no back affordance on the dialog |
| `AlarmSaveButton` | **`PrimaryButton`** | name is still `Save` |
| `AlarmNameTextBox` | *no AutomationId* | `Edit` with name `Alarm name` |

Confirmed present in the raw UIA tree (`System.Windows.Automation`, no driver involved) — the tree
grows 42 → 76 nodes when the dialog opens, inside the **same** Clock window:

```
Text   | aid=''              | name='Add new alarm'
Edit   | aid=''              | name='Alarm name'
Button | aid='PrimaryButton' | name='Save'
Button | aid='CloseButton'   | name='Cancel'
```

And WinAppDriver resolves all of them by both `accessibility id` and `name` while the dialog is
open, while the old ids return not-found.

So: `DismissAddAlarmPage()` hunts for `CancelButton`, falls back to `Back`, finds neither, throws
at `:175`, and `TestInit` fails. Every subsequent Alarms test inherits an app parked on the dialog.
**One renamed control costs 167 tests.**

Fixing the fixture is a three-line change: `CancelButton` → `CloseButton`,
`AlarmSaveButton` → `PrimaryButton`, and locate the name box by name rather than id. Do that and
re-run the control to get the real ceiling.

### Corrections made while chasing this — recorded so they are not repeated

Three wrong explanations were proposed and killed by measurement, in order:

1. "`AlarmButton` no longer exists on Win11" — false, it is in the tree.
2. "Our missing implicit wait causes it" — false, WinAppDriver has one and fails identically.
3. "The static `session` field, or a surviving single-instance app process, carries poisoned state"
   — false, the app terminates on `Quit()` and a fresh session recovers fully.
4. "WinAppDriver's `Invoke` click on `AddAlarmButton` is a silent no-op" — false, it opens the
   dialog reliably.

The lesson worth keeping: every one of these was plausible from the logs and wrong. The answer only
appeared from driving the real app step by step and checking state after each action.

**None of the rewrite plan depends on this.** It is a defect in the compatibility suite's fixture,
not in this driver. The measured 70-test backlog stands on its own.

**Either way this is a harness/app-lifetime defect, not app drift and not a driver bug**, and it is
not a ceiling: make the fixture recover (force-kill the app, or relaunch when the alarm tab cannot
be found) and a large fraction of the 143 currently-unmeasurable tests become measurable for both
drivers.

Note the irony: our driver's per-test relaunch bug (see [[015-winappdriver-protocol-contract]],
missing `window_handle` → `CurrentWindowIsAlive` returns false → session recreated every fixture
init) accidentally immunises us against this poisoning. That is why we *pass* 35 tests WinAppDriver
fails. Those are not wins — they are an artifact of a compatibility bug.

#### Superseded correction (kept so the reasoning is auditable)

An earlier pass today attributed this block to our missing implicit wait. That is wrong: WinAppDriver
has a working implicit wait and dies at the same line. What survives from that pass is narrower and
still true — our implicit wait *is* unimplemented (`/timeouts` at `SessionRoutes.cs:470` stores
nothing, `ElementFinder` never retries, `Timeouts.SetImplicitTimeout_FindElementFound` fails
standalone). It is a real defect, just an independent one.

#### The original claim, also wrong

The first version of this section claimed `AlarmButton` / `AlarmPivotItem` no longer exist on Win11.
That is **false**. Verified by walking the live UIA tree of
`Microsoft.WindowsAlarms 11.2606.11.0` on Win11 26200 (`System.Windows.Automation` walk from
PowerShell, so no involvement from this driver):

```
Group | aid='MenuItemsHost'
  ListItem | aid='FocusButton'    | name='Focus sessions' | class='Microsoft.UI.Xaml.Controls.NavigationViewItem'
  ListItem | aid='TimerButton'    | name='Timer'          | class='Microsoft.UI.Xaml.Controls.NavigationViewItem'
  ListItem | aid='AlarmButton'    | name='Alarm'          | class='Microsoft.UI.Xaml.Controls.NavigationViewItem'
  ListItem | aid='StopwatchButton'| name='Stopwatch'      | class='Microsoft.UI.Xaml.Controls.NavigationViewItem'
  ListItem | aid='ClockButton'    | name='World clock'    | class='Microsoft.UI.Xaml.Controls.NavigationViewItem'
```

`AlarmButton`, `StopwatchButton`, `ClockButton`, `AddAlarmButton`, `AppName` are all present, and
`ControlType` is still `ListItem` — so `AlarmTabClassName = "ListViewItem"` is the only genuinely
stale value in the fixture. The app is **not** the blocker.

**Actual blocker: this driver ignores implicit wait.** `AlarmClockBase.Setup()` sets
`ImplicitWait = 2.5s` expecting find to retry every 500ms. `ElementFinder.FindElement` does a
single `FindFirst` and throws `NoSuchElement` immediately; `POST /session/{id}/timeouts`
(`SessionRoutes.cs:470`) stores the value and nothing ever reads it. A UWP app mid-paint misses
on the first probe, `FindAlarmTabElement()` throws, `TestInit` falls into
`DismissAddAlarmPage()`, and that fails too — which is exactly the stack in `test-results-v2.txt`
(`AlarmClockBase.cs:79` → `:175`). `Timeouts.SetImplicitTimeout_FindElementFound` fails for the
same reason and is the isolated proof.

**Impact**: 167 of 290 tests (58%) inherit `AlarmClockBase`. They all die in `TestInit`, so the
suite has never measured them.

**Fix approach**: implement implicit wait in `ElementFinder` (retry until the deadline, then
throw). Then re-measure before touching the fixture — most of the "Alarms UI changed" work may
turn out to be unnecessary. Genuine app drift found so far is one string: the window title is
`Clock`, not `Alarms & Clock`.

### 2. CalculatorBase.GetStaleElement() Failing (~10 tests)
`CalculatorBase.GetStaleElement()` clicks buttons and opens the memory flyout to create a stale element. On Win11, the Calculator UI may have changed, causing `Click()` to fail with stale element reference at line 102.

**Impact**: `*Error_StaleElement` tests that use Calculator.

### 3. `location_in_view` Tests (~6 tests)
`get_LocationOnScreenOnceScrolledIntoView()` in Selenium 3.8 base `RemoteWebElement` is NOT overridden by the Appium driver. It uses `response.Value as Dictionary<string, object>` which fails because Newtonsoft.Json deserializes to `JObject`, not `Dictionary`.

**Fix approach**: Either return a format that Newtonsoft.Json deserializes to `Dictionary`, or accept that these tests can't pass with the old client.

### 4. `SendKeys` Tests (~10 tests)
`session.Keyboard.SendKeys()` crashes with `NullReferenceException` in `AppiumCommandExecutor.Execute`. This is a client-side bug in the old Appium driver library. Our `/keys` endpoint works fine via direct HTTP.

**Not fixable** without modifying the Appium client library.

### 5. Actions/Pen/Touch Tests (~30 tests)
Not implemented. Large effort, low ROI. These use the W3C Actions API which is complex.

### 6. Window Management Tests (~15 tests)
Some window tests may fail due to DPI scaling differences or window state management.

## Test Environment Notes
- Visual Studio 2026 on F: drive (`F:\VisualStudio2026`)
- MSTest runner at `F:\VisualStudio2026\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe`
- Tests are pre-built .NET Framework 4.8, rebuild via MSBuild at `F:\VisualStudio2026\MSBuild\Current\Bin\MSBuild.exe`
- Test code CAN be modified (it's source, not binary)
- WinAppDriver test packages restored in `WinAppDriver/Tests/WebDriverAPI/packages/`

## Most Impactful Next Steps
1. Fix Alarms app detection for Win11 → would unblock ~30 stale element tests
2. Fix CalculatorBase.GetStaleElement() → would unblock ~10 more tests
3. Investigate `SendKeys` client-side crash → may need to modify test code

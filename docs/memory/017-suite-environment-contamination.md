# 017 — The compatibility suite contaminates its own environment

**Date:** 2026-08-10
**Status:** measured, with a control on both sides

## The finding

A compatibility-suite score is not a property of the driver alone. It is a
property of the driver **and** how much residue previous runs left in Alarms &
Clock. Two distinct contaminations were measured in one day, in opposite
directions.

## 1. The alarm cap silently costs exactly nine tests

**Corrected: the suite's cleanup is fine; we break it.** WinAppDriver runs the
whole suite from a fresh store and leaves exactly the one default alarm. Under our
driver about **162** accumulated in a day, because
`DeletePreviouslyCreatedAlarmEntry` calls `FindElementByXPath` (we answer status
19) and `Mouse.ContextClick` — `/moveto` then `/click` (we answer status 9,
command not recognized) — inside a `catch { break; }`, so every failure is silent.

With that many alarms the app disables **only** `AddAlarmButton` (threshold and
mechanism unmeasured):

```
AddAlarmButton                  enabled=False    <- only this one
SelectAlarmsButton              enabled=True
MoreButton                      enabled=True
AlarmCollectionPageCommandBar   enabled=True
```

Nothing about the app looks broken. Nine tests need that button, all deriving
from `AlarmClockBase`:

```
ClearElement                                 GetElementText
FindElements_ByName                          ClearElementError_StaleElement
ClickElementError_StaleElement               GetElementAttributeError_StaleElement
GetElementDisplayedStateError_StaleElement   GetElementSelectedStateError_StaleElement
GetElementTextError_StaleElement
```

**Confirmed by manipulation.** Same commit `21a18dd`, environment the only
variable: with the store capped the nine failed; with the store reset all nine
passed.

**Six of them are mislabelled.** They are named `*Error_StaleElement` but never
reach that behaviour. `GetStaleElement()` clicks `AddAlarmButton` then finds
`AlarmSaveButton`; when that find fails it throws from inside the helper, and the
test's `catch (InvalidOperationException)` — written to inspect the driver's
stale-element message — catches the setup failure and asserts on it instead:

```
Expected:<An element command failed because the referenced element is no longer attached to the DOM.>
Actual:  <An element could not be located on the page using the given search parameters.>
```

That reads exactly like returning error 7 where error 10 was required. It is not.
**Read the stack, not the assertion.**

The alarms are **not** in `LocalState` — clearing that changes nothing, and was
tried first. They live in the UWP settings hive:

```
%LOCALAPPDATA%\Packages\Microsoft.WindowsAlarms_8wekyb3d8bbwe\Settings\settings.dat
```

## 2. Resetting cold-starts the app, which costs sixteen more

The first run that reset the store scored **118**, *lower* than the 124 it
replaced, despite gaining the nine. All sixteen `ActionsError_*` tests — pure
payload-validation tests with no connection to alarms — flipped to failing:

```
Initialization method WebDriverAPI.Actions.TestInit threw exception.
System.InvalidOperationException: Currently selected window has been closed.
   at WebDriverAPI.AlarmClockBase.DismissAddAlarmPage()
```

The failure begins with the **first test of the run**, so nothing killed the
window mid-run — the session never had a usable one. Resetting closes the app,
so the suite cold-started it; every earlier run had quietly inherited a warm
Alarms & Clock left behind by its predecessor.

So the runner must reset the store **and then warm the app**, waiting for
`AddAlarmButton` to be *enabled* rather than merely present — present-but-disabled
is the cap state from part 1. `tools/vm/Invoke-CompatibilitySuite.ps1` does both
and pins the commit.

## 3. The driver bug this exposed

With `AddAlarmButton` disabled, `InvokePattern.Invoke()` threw, so the click
ladder fell through to its ancestor walk, reached
`AlarmCollectionPageCommandBar` — which advertises `Toggle` and `ExpandCollapse` —
and toggled it. The app bar opened and closed while the driver answered
`status 0`.

Fixed: a disabled element is refused with `NotInteractable` before any rung runs,
before scrolling and before foregrounding, so a refusal has no side effects. The
regression test uses a disabled `Button` inside a `CheckBox` — the same shape —
and asserts the **bystander**, because "refused" and "climbed and toggled the
parent" both leave the disabled button untouched.

## What this invalidates

**Every compatibility number for THIS driver measured before 2026-08-10 was
taken on an uncontrolled environment.** The WinAppDriver baseline turned out not
to be: re-measured with the store freshly reset, 1.2.1 scores **281 again**, the
same nine failures. Its score does not depend on the contamination — only ours
did. I had assumed otherwise and written a caveat saying so; measuring it removed
the caveat rather than confirming it.

The matched pair is therefore **281/290 for WinAppDriver 1.2.1 against 133/290
for this driver**, same guest, same suite DLL, store reset for both.

The prediction table's large gains (`/timeouts` +62, window reads +28) are far
outside this effect. The **+15 credited to Actions validation is exactly the
sixteen tests that move with app warmth**, so that credit is now doubtful and
should be re-measured under a controlled environment before being quoted.

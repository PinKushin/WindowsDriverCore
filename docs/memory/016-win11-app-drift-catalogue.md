# 016 — App Drift Catalogue (was: Win11)

## Date: 2026-08-08, amended 2026-08-11

> **AMENDMENT 2026-08-11 — this is no longer a Windows 11 document.** The Windows 10
> guest's Alarms & Clock **updated to `11.2606.11.0`**, the same version catalogued
> below, on 2026-08-10 between the 13:53 WinAppDriver baseline run and the runs that
> resumed at 17:12; the package folder under `C:\Program Files\WindowsApps` was created
> at 16:38. Every Alarms rename in the table below now applies to the guest as well.
>
> Verified on the guest through WinAppDriver itself: it clicks `AddAlarmButton`, and
> then repeated one-shot finds for `AlarmSaveButton` with `implicit_wait=0` never
> succeed across 8 s. `PrimaryButton` (`Name="Save"`, `IsControlElement="True"`)
> appears at 3172 ms. So the entry below was right and this is not a driver defect,
> not a tree-view problem, and not something an implicit wait would rescue.
>
> **Consequence: the 281/290 baseline was measured against a different application
> than every score taken after 2026-08-10 16:38.** Comparisons across that boundary
> are invalid, in both directions. The guest was chosen as the instrument because the
> suite was readable there (281 against 112 on Win11); the part of that advantage that
> came from an older Alarms build is gone. Record the app version next to any score,
> and consider stopping Store auto-update on the guest — an instrument that updates
> itself is not an instrument.

Every difference found between what the WinAppDriver test suite expects and what Windows 11 26200
actually exposes. Recorded here because the `WinAppDriver/` working tree is being reverted to
pristine — that suite is the untouched external scoreboard from now on, so this knowledge has to
live somewhere else.

**Use this when writing our own tests.** Our suite carries the *behaviour* those tests verify, not
their automation ids. Where a test's contract is "SendKeys puts text in an edit control", our
version asserts that against a control we locate robustly; the specific id below is a fact about
one app version, not a contract.

## Alarms & Clock (`Microsoft.WindowsAlarms` 11.2606.11.0)

The add/edit alarm page was rebuilt as a WinUI `ContentDialog`. It lives in the **same** Clock
window — the UIA tree grows 42 → 76 nodes when it opens — so this is not a separate-window or
tree-visibility problem. The controls were simply renamed.

| Suite expects | Win11 actual | Notes |
|---|---|---|
| `CancelButton` | **`CloseButton`** | `Name` is still `Cancel` |
| `Back` | *gone* | dialog has no back affordance at all |
| `AlarmSaveButton` | **`PrimaryButton`** | `Name` is still `Save` |
| `AlarmNameTextBox` | *no AutomationId* | `Edit` with `Name` = `Alarm name` |
| `EditAlarmHeader` text `"NEW ALARM"` | `"Add new alarm"` | genuine assertion drift, not a locator |

Still correct on Win11 (verified against the live tree): `AlarmButton`, `StopwatchButton`,
`ClockButton`, `TimerButton`, `FocusButton`, `AddAlarmButton`, `EditAlarmsButton`, `AppName`,
`AlarmToggleSwitch`, `SignInButton`. `ControlType` of the nav items is still `ListItem`, though
`ClassName` is now `Microsoft.UI.Xaml.Controls.NavigationViewItem` rather than `ListViewItem`.

Window title is **`Clock`**, not `Alarms & Clock`.

This single rename cascade is what costs the suite ~167 tests: `DismissAddAlarmPage()` hunts for
`CancelButton` then `Back`, finds neither, throws, and `TestInit` fails for everything downstream.
See [[009-test-progress]] for the full chain and for the four wrong explanations that were proposed
and killed before this one.

## Calculator (`Microsoft.WindowsCalculator` 11.2606.0.0)

`CalculatorBase.Setup` asserts the mode header equals `"Standard"`. Win11 reports
**`"Standard Calculator mode"`**. An `IndexOf("Standard Calculator")` check covers both, but note
it is a *weaker* assertion than the original equality — if our own suite needs this, assert exact
equality against whichever string the target build actually produces rather than substring-matching.

`Header` may not exist; the suite already falls back to `ContentPresenter`.

## Notepad (`Microsoft.WindowsNotepad` 11.2606.15.0)

Win11 Notepad is now a Store app and its edit surface changed class:

| Suite expects | Win11 actual |
|---|---|
| `ClassName` = `Edit` | **`RichEditD2DPT`** |

`C:\Windows\System32\notepad.exe` still exists (10.0.26100) but is a launcher stub that redirects
to the packaged app, so launching by path still lands on the new UI.

## Microsoft Edge — unrecoverable

`CommonTestSettings.EdgeAppId` is `Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge` — legacy
EdgeHTML. It does not exist on Win11 at any version. The 3 `EdgeBase` tests
(`TouchClick` ×2, `TouchFlick` ×1) cannot pass and never will. Our suite should not carry them;
if browser coverage is wanted, target Chromium Edge deliberately as a new scenario.

## What is *not* drift

Worth recording because each was suspected and cleared by measurement:

- WinAppDriver opens **and** closes the Add Alarm dialog correctly when given `CloseButton`. No
  click-semantics defect, no `Invoke` no-op.
- The dialog is in the same window's UIA tree. Not `#857`, not a popup-window problem.
- Alarms terminates on `DELETE /session` and a fresh session recovers fully. No surviving-process
  state poisoning.

Relates to [[009-test-progress]], [[015-winappdriver-protocol-contract]].

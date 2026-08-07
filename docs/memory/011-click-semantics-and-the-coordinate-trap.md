# 011 — Click Semantics: Why Pattern-First Is Right, and What Is Still Missing

## Date: 2026-08-06

## Source

Field evidence from PokemonBattleJournal's Windows UI suite (83 Appium tests, MAUI/WinUI 3 app),
where a two-day-old flake was traced to exactly the behaviour this project replaces. The design
here is **validated**, and three concrete gaps surfaced.

## The bug this project's design avoids

`WinAppDriver`'s *Element Click* is **synthesized mouse input at the element's bounding-box
centre, in SCREEN coordinates**. It does not scroll first and does not check that the point is
inside the target window.

Consequences observed:

- An element laid out below the window bottom was clicked at a screen position belonging to
  another window. At a 754x512 window on a 1920x1080 desktop, elements ~545px below the fold
  produced clicks at y≈1057 — **the taskbar**. Runs launched Visual Studio and the Epic Games
  store mid-suite.
- On CI, where the app fills the desktop and nothing sits behind it, the identical click lands
  on empty desktop and **returns success**. The symptom is: click dispatched, ~1000ms elapsed,
  no handler ran, and every find-only test keeps passing because *finding* an off-viewport
  element works fine.

That second case is the dangerous one — a silent no-op reported as a successful click. Any
driver that clicks by coordinate inherits it.

**Confirmation:** replacing the coordinate click with a UIA pattern ladder took that suite from
failing-with-stray-app-launches to **83/83 at the same window size**. Patterns carry no
coordinates, so window bounds cannot make them miss.

W3C WebDriver also specifies scroll-into-view before Element Click; WinAppDriver predates and
ignores that.

## Gap 1 — `TogglePattern` is missing from the ladder

Current order in `ElementInteractor.Click`: Invoke → SelectionItem → ExpandCollapse → SetFocus.

**`CheckBox` and `Switch` expose `TogglePattern` and NOT `InvokePattern`.** They currently fall
all the way through to `SetFocus()` and silently do nothing.

```csharp
var toggle = element.TryGetPattern<IUIAutomationTogglePattern>(UIAPatternIds.UIA_TogglePatternId);
if (toggle is not null) { toggle.Toggle(); return; }
```

`UIA_TogglePatternId = 10015`. Place it after Invoke, before SelectionItem.

## Gap 2 — the final fallback fails silently

`element.SetFocus()` then `return` reports success while doing nothing the caller asked for.
That is the same class of defect as the coordinate click above: an operation that cannot be
distinguished from a working one.

It should either throw `ElementNotInteractable`, or perform a **guarded** mouse click (Gap 3).
Focusing and claiming success is the one option that should not survive.

## Gap 3 — no mouse fallback at all, and MAUI needs one

A pattern-only driver cannot click a large share of a real MAUI app. **A `Border`, `Grid` or
`Image` with a `TapGestureRecognizer` exposes no pattern whatsoever** — it is a hit-test target
and nothing more. In PokemonBattleJournal that covered a custom toggle switch and every item in
a picker popup; those are ordinary MAUI idioms, not exotic ones.

So a mouse fallback is required for real-world coverage. If added, it must not repeat
WinAppDriver's mistake:

1. `ScrollItemPattern.ScrollIntoView()` first when supported (`UIA_ScrollItemPatternId = 10017`).
2. Re-read `CurrentBoundingRectangle` **after** scrolling — the pre-scroll rect is stale.
3. Compare the click point against the **window** rect, not the desktop.
4. If it still falls outside, **throw** `ElementNotInteractable` naming both rects. Never
   dispatch. A click that leaves the window is input sent to another application, and on a CI
   machine it silently accomplishes nothing.

That guard is what converts an invisible flake into a diagnosable error, and it is the single
most valuable behavioural difference from WinAppDriver.

## Suggested final ladder

| Step | Pattern | ID | Covers |
|---|---|---|---|
| 0 | ScrollItem (if supported) | 10017 | bring into view first |
| 1 | Invoke | 10000 | buttons, links |
| 2 | Toggle | 10015 | checkboxes, switches |
| 3 | SelectionItem | 10010 | list/tab items |
| 4 | ExpandCollapse | 10011 | combos, menus |
| 5 | guarded mouse click | — | gesture-recognizer elements |
| 6 | throw ElementNotInteractable | — | never a silent success |

## Note on FlaUI

FlaUI is a managed wrapper over this same `IUIAutomation` COM API — its `AutomationElement`
exposes `.Patterns.Invoke.Pattern.Invoke()` (pattern, coordinate-free) separately from
`.Click()` (real mouse via `Mouse.Click` at the element centre). The distinction it draws
between those two is the useful idea; the implementation underneath is the same
`GetCurrentPattern` call this project already makes directly. There is nothing to take from
FlaUI's code — raw COM is strictly closer to the metal and avoids a dependency.

## Compatibility caveat — this project tests against WinAppDriver's own suite

Pattern-first Click is more reliable, but it is **not behaviourally identical** to a mouse
click, and a compatibility contract has to account for the differences:

- **Occlusion.** A real click hits whatever is on top; W3C specifies
  `element click intercepted` when the target is covered. `Invoke()` bypasses occlusion
  entirely and succeeds on a covered element. If the WinAppDriver suite asserts intercepted
  clicks, pattern-first will pass where it should fail.
- **Focus.** A mouse click focuses the control as a side effect; `Invoke()` does not. Apps that
  depend on focus-follows-click (validation firing on blur, a picker committing on focus loss)
  will behave differently.
- **Hover.** No pointer movement means no hover state, so anything gated on
  `PointerOver` never triggers.

None of these bit the MAUI suite — 83/83 after the change — but that app has no
occlusion-dependent tests. Recommendation: keep the ladder as the default because it fixes a
whole class of silent failure, and treat the three cases above as known, documented divergences
rather than discovering them through a red compatibility run.

## Consider exposing both

Tests occasionally need genuine mouse input — verifying a `TapGestureRecognizer` actually fires,
or hit-testing overlay behaviour. Consider a vendor-extension endpoint (`windows: invoke` vs
`windows: mouseClick`) so callers can choose deliberately, with W3C *Element Click* defaulting
to the safe ladder.

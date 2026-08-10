# Click Semantics — the design requirement, and the field evidence for it

**This is the one thing this project can demonstrably do better than
WinAppDriver.** It has a documented cause, a known reproduction, and a measured
before/after from a real application suite. Unlike #857 and #1079 — see
`docs/FOUNDING-PREMISE.md` — nothing here is inference.

---

## What WinAppDriver does

`Click()` is **synthesized mouse input at the element's centre, in SCREEN
coordinates**. It does not scroll first and does not check the point lands
inside the target window.

Consequences, all directly observed in PokemonBattleJournal's Windows suite:

- **Locally**, an element laid out ~545px below the window bottom at a 754x512
  window on a 1920x1080 desktop was clicked at y≈1057 — the taskbar. Runs
  launched Visual Studio and the Epic Games store mid-suite, and pulled a browser
  window to the front.
- **On CI**, where the app fills the desktop and nothing sits behind it, the
  identical click lands on empty desktop and **returns success**. Dispatched,
  ~1000ms elapsed, no handler ran.

The CI symptom is the dangerous one. A silent no-op reported as a successful
click, while every find-only test keeps passing — because *finding* an
off-viewport element works fine.

## The second failure, and the subtler one

From the same investigation:

> ReadJournal rows put their `AutomationId` on a `Border` **inside** the
> CollectionView item container, and the container is what holds
> `SelectionItemPattern` — the id named a child with no pattern while its parent
> was perfectly selectable.

So the element is found, is on screen, and is genuinely selectable — and a
pattern-based click still finds nothing to invoke, because the pattern lives one
level up. Those rows had **no accessibility defect**: `SelectionMode="Single"`
means a screen reader could always select them. It was purely an
automation-lookup mismatch.

**This is what "cannot click anything in the CollectionView" actually was.** Not
a find problem, not a caching problem.

## What is the driver's fault, and what is not

An earlier version of this document claimed the application's transparent
`Button` overlays were an indictment of the driver. **That was wrong, and it is
worth being precise about, because the two failures above have different causes
and only one of them is the driver's.**

**Not the driver's fault: a composite control has no pattern to find.** A MAUI
`Grid` or `Border` with a `TapGestureRecognizer` exposes no UIA pattern because
MAUI does not give it one. When an app builds a custom control out of primitives
— PokemonBattleJournal's archetype dropdown shows *two* images beside the
selection, which no stock `Picker` does — the result is a composition, not a
ComboBox, and UIA has nothing to report about it. There is literally nothing for
any driver to invoke.

Overlaying a transparent `Button` that owns the `AutomationId` and the command is
therefore not a workaround for a broken driver. It gives the composite a real UIA
control type, which **assistive technology needs just as much as automation
does** — a screen reader cannot announce "button" for something UIA calls a pane
either. It is reasonable design.

The consequence for this driver is the opposite of what the earlier framing
suggested: because genuinely pattern-less targets exist and always will, a mouse
path is **necessary**, not a failure. A pattern-only driver cannot click a large
share of a real MAUI application.

**The driver's fault, and the actual defect:** the mouse path is *unguarded*. It
clicks at screen coordinates without scrolling first and without checking the
point lands inside the target window, then reports success regardless. That is
what fired clicks into the taskbar locally and did nothing at all on CI.

**Also the driver's fault: the ancestor lookup.** The CollectionView rows were a
different problem entirely — a pattern *did* exist, one level up, and the driver
never looked. The investigation is explicit that this was fixed in the test
helper and not in the app markup, precisely because the rows had no accessibility
defect: `SelectionMode="Single"` meant a screen reader could always select them.
Worth asking before changing any app markup — is the element genuinely
unreachable, or merely mis-identified?

**On FlaUI.** It was reached for where WinAppDriver could not do the job. FlaUI
wraps the same `IUIAutomation` API this driver uses, and draws an explicit
distinction between a pattern invoke and a real mouse click. That distinction is
the idea worth taking; it is evidence about what a driver should expose, not
evidence that WinAppDriver was hiding a capability.

---

## The required ladder

Order matters. Each step is here because something in a real suite needed it.

| Step | Pattern / action | Covers |
|---|---|---|
| 0 | `ScrollItem.ScrollIntoView` | bring into view before anything else |
| 1 | `Invoke` | buttons, links |
| 2 | `Toggle` | checkboxes, switches — these expose Toggle and **not** Invoke |
| 3 | `SelectionItem` | list rows, tab items |
| 4 | `ExpandCollapse`, after `Focus()` | MAUI `Picker` → WinUI ComboBox |
| 5 | `Focus()` when ControlType is Edit or Document | clicking a text input *means* focusing it |
| 6 | **the same ladder on up to 3 ancestors** | the id sits on a child of the element that holds the pattern |
| 7 | guarded mouse click | last resort, for genuinely pattern-less targets |
| 8 | throw `ElementNotInteractable` | never a silent success |

**Step 6 is the one that is easy to miss and the one that fixed the
CollectionView.** Without it, an id on a pattern-less child falls straight
through to the mouse path — which is the original defect.

**Step 7 must be guarded**, and the guard is the point:

- scroll into view first
- **re-read the bounding rect after scrolling** — the pre-scroll rect is stale
- compare the click point against the **window** rect, not the desktop
- if it still falls outside, **throw, naming both rects**. Never dispatch.

A click that leaves the window is input sent to another application. On a
developer's machine that launches Visual Studio; on CI it silently accomplishes
nothing. Refusing, loudly, is what turns an invisible flake into a diagnosable
error.

**Step 8 matters as much as any pattern.** The implementation being replaced
ended its ladder with `SetFocus()` and returned success — an operation
indistinguishable from a working one, which is the same defect class as the
coordinate click.

---

## Why pattern-first is a determinism argument, not only a capability one

Owner's direction, 2026-08-09: *route clicks through the UI tree like FlaUI does
where it can click things off screen and not use the mouse — user interference
doesn't matter then. But we have to have mouse automation too, if for nothing
more than backwards compatibility.*

The capability half is already above: patterns reach elements a coordinate
cannot. The half worth writing down is that **a coordinate click is not
deterministic on a machine with a human on it**, and the reason is narrower and
more dangerous than "the mouse might move".

**What is not a risk.** `SendInput` documents that the events in one call are
*"not interspersed with other keyboard or mouse input events inserted either by
the user (with the keyboard or mouse) or by calls to keybd_event, mouse_event,
or other calls to SendInput"*. So a **single call carrying move, button-down and
button-up cannot be split by the user's hand.** Issuing three separate calls
gives up that guarantee for nothing. One batch, always.

**What is the risk, and it is not about the cursor at all.** Between reading the
element's rectangle and issuing the batch, the *coordinate* can stop meaning what
it meant:

- the window is dragged, by a person or by the application
- the application re-lays out — a virtualised list scrolls, a panel expands
- another window is raised over the point

None of these are interrupted by `SendInput`'s serialisation guarantee, because
nothing about the input stream is wrong. The batch is delivered exactly as
composed, to whatever is now under that point, and the driver reports success.
That is the CI failure from the field evidence above wearing a different hat:
**a click that lands somewhere else is indistinguishable from a click that
worked**, unless the driver checks.

So the mitigations are about the coordinate, not the pointer:

- read the rectangle **immediately** before the batch, after any scrolling
- check the point is inside the **target window**, and refuse loudly if not
- never retry. A retry converts a deterministic wrong answer into a
  probabilistic right one and destroys the signal.

And the conclusion for the ladder: the pattern path is immune to every item on
that list, because it names the element rather than a location. **It is the
correct default for reliability reasons even where a mouse click would also
work.** The mouse path exists because pattern-less targets exist — see the MAUI
composite above — and for callers who specifically need real pointer input.

**Consequence for the caller.** Because the two paths differ in kind and not
just in mechanism, which one ran should be selectable rather than inferred:
`windows: invoke` for pattern-only, `windows: mouseClick` for real pointer input,
with W3C *Element Click* defaulting to the ladder. A test asserting that a
`TapGestureRecognizer` fires, or that a hit-test overlay behaves, genuinely needs
the mouse and should be able to say so.

**Consequence for this project's own tests.** Any test that depends on a window
staying where it was put is exposed to the same interference. They should report
what moved when they fail, so a human-caused failure is recognisable as one
rather than being filed as a driver defect.

---

## Field confirmation, and one detail to copy

Checked against PokemonBattleJournal's own memories on 2026-08-10.

**Note the horizon.** Those memory files cover roughly one month — they start
when the project began working with newer AI. The application has been driven
through WinAppDriver for years, but that older experience lives in the git
history, in the test helpers themselves, and in the owner's recall. **For a
WinAppDriver behaviour older than about a month, ask rather than grep**: the
memories will be silent and silence there is not evidence of absence.

**The ladder here matches theirs rung for rung** — ScrollIntoView, Invoke,
Toggle, SelectionItem, ExpandCollapse-after-focus, Focus for Edit and Document,
the same patterns on up to three ancestors, then a guarded mouse. That is
independent corroboration rather than agreement, because theirs was arrived at by
fixing a two-day flake and ends at 83/83 on CI's 754x512 geometry, where it had
previously fired clicks into Visual Studio and the Epic Games store.

**The detail worth copying, which this document did not specify:** their guarded
mouse *"refuses if outside the window under **both coordinate spaces**"*. This
document says only to compare against the window rect. Given that `/location` is
window-relative while `BoundingRectangle` and synthesized input are screen
coordinates — see `PROJECT-KNOWLEDGE.md` — checking one space is exactly the kind
of half-guard that passes on a window at the top-left of the primary display and
fails everywhere else. **The mouse rung must check both.**

**A stale claim to distrust if it comes up again.** An earlier memory there,
`feedback_flaui_scroll_into_view` (2026-08-03), states that WinAppDriver's
`Click()` uses InvokePattern for Button-typed elements. The later investigation
(2026-08-06) disproves it: `Click()` is synthesized mouse input, and the pattern
ladder they now use lives in *their test helper*, driving UIA directly rather
than through WinAppDriver. The earlier line reads authoritative and would
undermine this project's central claim if taken at face value — it was nearly
used here to "correct" the section above into being wrong.

**Also corroborated: duplicate automation ids resolve to the ancestor.** Their
notes record that copying an `AutomationId` onto an overlay button changed
nothing, because "a lookup resolves the **parent** first". That is tree order,
and it is what `POST /element` returns now that it uses `FindFirst` — the same
element `FindAll(...)[0]` would have given. A suite written against WinAppDriver
depends on that ordering.

---

## Known divergences from a real mouse click

Pattern activation is not identical to a mouse click, and the differences should
be documented rather than discovered during a compatibility run:

- **Occlusion.** `Invoke()` succeeds on an element covered by something else.
  W3C specifies `element click intercepted` for that case; a real click hits
  whatever is on top.
- **Focus.** A mouse click focuses the control as a side effect; `Invoke()` does
  not. Applications relying on validation-on-blur or commit-on-focus-loss behave
  differently.
- **Hover.** No pointer movement means no hover state, so anything gated on
  `PointerOver` never fires.

None of these bit the MAUI suite — 83/83 after the change — but that app has no
occlusion-dependent tests.

**Consider exposing both deliberately.** A vendor extension
(`windows: invoke` versus `windows: mouseClick`) lets a caller choose, with W3C
*Element Click* defaulting to the safe ladder. Tests that specifically verify a
`TapGestureRecognizer` fires, or that hit-test overlay behaviour, genuinely need
real pointer input.

---

## The experiment this makes possible

Unlike H1, this one has a sharp prediction and a known-positive reproduction.

- **Manipulation:** click implementation — ladder versus coordinate.
- **Condition:** (a) an element below the fold in a deliberately small window,
  and (b) an element whose `AutomationId` sits on a pattern-less child of a
  container that holds `SelectionItemPattern`.
- **Measurement:** did the handler run — observed through app state, not through
  the driver's own report of success.
- **Control:** the same element and geometry through real WinAppDriver, which is
  documented and observed to miss.
- **Effect size:** the window must be small enough for the target to fall
  outside. 754x512 on a 1920x1080 desktop is the measured case. A maximised
  window will not reproduce it, which is why the earlier geometry investigation
  wrongly concluded the theory was dead.

That last point is worth keeping: a negative result is only worth the fidelity of
the setup that produced it.

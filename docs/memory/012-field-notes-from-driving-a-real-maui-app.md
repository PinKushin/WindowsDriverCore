# 012 — Field Notes from Driving a Real MAUI App

## Date: 2026-08-06

## Source

PokemonBattleJournal: MAUI / WinUI 3, unpackaged, 83 Windows Appium tests plus 82 on Android,
driven daily for weeks against WinAppDriver. These are behaviours that only show up against a
real app over time — each one cost hours to diagnose there, and each is something this driver
either has to reproduce (compatibility) or can deliberately do better.

## Phantom elements: found, but with a null/empty id

During a `CollectionView` rebind — a row deleted, the list re-materialising — WinAppDriver
returns an element that is **in the tree but has no usable id**. Reading it throws
`InvalidOperationException` client-side rather than any WebDriver error, so a caller that
catches `NoSuchElementException` does not catch this.

The suite had to defend with a dedicated catch around presence checks:

```csharp
try { stuck = IsElementPresent("Busy_Mutating"); }
catch (InvalidOperationException) { return; }   // caught mid-removal
```

**For this driver:** an element caught mid-teardown should map to
`stale element reference`, which is the error callers already handle. Returning something
half-formed pushes the problem onto every caller, and each one invents a different guard.

Relates to `004-element-identity` — worth treating "element exists but its identity is not yet
resolvable" as a distinct state from alive and from stale.

## The cached snapshot is a real source of missing elements

`#1079` (random empty FindElements) reproduced constantly. The workaround that shipped:
query the **live** UIA tree directly, and once it confirms the element exists, spin-re-anchor
WinAppDriver until its own lookup agrees.

That is direct evidence for this project's premise — the element was always in the tree; only
the wrapper's cached view lacked it. Since raw COM queries live by default, this whole class
should simply not exist here. **Worth an explicit regression test**: build a tree, mutate it,
and assert `FindElements` never returns empty for an element that is present.

## WinAppDriver DOES auto-scroll on click — inconsistently

Easy to conclude it never scrolls (see `011`, where clicks land off-window). Not true: for
elements inside a container exposing `ScrollItemPattern` it scrolls into view before clicking.

Measured consequence: a test that read an element's `Location`, clicked, then read again saw a
**132px difference** — not a layout bug, the driver had scrolled the page. A test comparing a
before-click position to an after-click position is comparing two different scroll offsets.

**For this driver:** whatever the policy is, make it *uniform* and document it. The damage comes
from it being conditional — sometimes the coordinate is stale, sometimes not, with nothing in
the response saying which.

## MAUI `Picker` dropdowns are separate top-level windows

Selecting from a `Picker` was originally done by enumerating `WindowHandles` to find the popup.
That was deleted; it is keyboard navigation now — click, type the item's first letter, `Tab` to
commit, never `Enter` — ~330ms and reliable.

Two things for the driver:

- A popup that is its own top-level window is invisible to a search rooted at the app window.
  Element search needs an explicit policy for popups/menus/dropdowns, and it should be stated
  rather than emergent.
- `Enter` on a committed picker submitted the surrounding form in some layouts. Whatever
  key-injection surface exists, `Tab`-to-commit is the safer default for the docs.

## Implicit wait is charged in full for every absent element

An element that is present resolves in ~215ms; an element that is **absent** costs the entire
implicit wait — 5s ambient meant ~6.8s per miss. A fixture doing a dozen optional-element checks
spent a minute waiting on things it expected not to find.

The suite fixed this by dropping the implicit wait to zero for optional lookups only.

**For this driver:** a "does this exist right now" query is a legitimate and common need. Either
support a per-request timeout override on find, or document loudly that callers must set the
timeout to zero themselves. Making absence expensive by default is a performance trap that
silently costs minutes per run.

## Element text: what `GetText` should return

On Windows an `Editor`'s text is the value alone. On Android the same element returns
`content-desc + ", " + value` — the accessibility description prepended — so
`"Notes for game 1 of the selected match, Seed-BO1-1"`.

Cross-platform assertions had to anchor to the **end** of the string, never the start, and that
only became apparent when an assertion passing on Windows failed on Android.

**For this driver:** pick one and state it. If `GetText` returns the value, say so; if it
concatenates the name, say that. Silence here produces platform-specific test code by accident.

## Window sizing works, and geometry belongs in the logs

`Manage().Window.Size` and `.Position` behave correctly and were essential — a CI-only failure
was only reproducible once the window could be forced to CI's exact 754x512.

Two lessons worth carrying:

- Windows **cascades** each new window ~26px down-right, so consecutive local runs start at
  different origins. Anything reproducing a geometry-sensitive bug must pin the position too, or
  every run tests a different geometry.
- The single highest-value diagnostic added all session was logging the window rect at session
  start. Every prior investigation had *guessed* the runner's geometry from its desktop
  resolution and guessed wrong. **Consider putting window rect and element rect in the session
  and error payloads by default** — a driver that reports where it thought things were makes
  this class of bug self-diagnosing.

## Meta-lesson for the compatibility suite

Two bugs in that app hid each other for weeks: unsorted match history plus phantom duplicate
child objects. A test clicked row 0 believing it was newest, got the oldest, and passed anyway
because the phantoms made the expected views appear. Fixing **either** alone turned the suite
red; fixing both made it pass honestly.

Worth remembering while chasing WinAppDriver parity: when a fix makes a previously-green test
fail, the first question is whether that test was ever passing for the right reason.

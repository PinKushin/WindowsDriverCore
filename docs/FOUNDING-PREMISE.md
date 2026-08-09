# What This Project Actually Fixes

**Written 2026-08-08 after reading the two issues the project was founded on.
Nobody had read them. Everything below supersedes the summaries in `CLAUDE.md`
and earlier memory entries, which are wrong in three specific ways.**

---

## What was believed

From this repo's `CLAUDE.md`, carried forward into every memory entry and into
the rewrite spec:

> - **#857**: Elements exist on screen but aren't in the UIA tree that
>   `FindElement` searches. ListView items get "orphaned".
> - **#1079**: `FindElements` randomly returns empty results.
> - Both are believed to come from the managed wrapper's cached view of the tree,
>   not from UIA itself. Querying the live tree with raw COM should make the whole
>   class not exist.

That last sentence is the entire justification for the architecture. It was
never verified.

## What the issues say

### #857 — elements not in the tree

The reporter's own words: *"when using inspect, even on first arrival, we CAN
use the mouse to select the listview… it seems to be an orphaned element"*
because *"there is no parent in that tree."*

And the detail that settles it: **Inspect.exe also fails to show the elements.**
It resolves after navigating away and back.

`inspect.exe` is a raw UI Automation client. It holds no WinAppDriver cache and
no managed wrapper. If Inspect cannot see the element, the element is genuinely
absent from the UIA tree — the application's UIA provider has not published it
yet.

**Conclusion: this driver does not fix #857 and cannot.** Any UIA client sees the
same empty tree. The only mitigations available to a driver are retrying (an
implicit wait, which we owe anyway) and possibly walking a different tree view,
neither of which is a fix.

### #1079 — FindElements returns empty while FindElement does not

Not random, and not about caching. The report is a **deterministic asymmetry
between two endpoints for the same query**, against Outlook 365 with
WinAppDriver 1.2:

```
POST /element   //*[@ClassName='SuperGrid']//*[@ClassName='ThreadItem']
  -> "value":{"ELEMENT":"42.854534.4.-384"}

POST /elements  //*[@ClassName='SuperGrid']//*[@ClassName='ThreadItem']
  -> "value":[]
```

Same expression, same app, same moment. One endpoint finds it, the other does
not.

That is a plausible defect in **WinAppDriver's own XPath evaluation**, which has
separate singular and plural code paths. It has nothing to do with a cached view
of the tree.

**Conclusion: #1079 is real, is probably fixable, and is an XPath bug.** We have
not implemented XPath at all, so we currently neither fix nor reproduce it.

---

## Why the experiment failed

`FindStabilityComparisonTests` used `accessibility id` under tree mutation and
measured 0 empty results out of 300 for both drivers. Of course it did — that
condition has nothing to do with either issue. The real #1079 needs an XPath
expression with a descendant axis, evaluated through both endpoints.

This is the second time a condition was chosen from a belief about the cause
rather than from the evidence. The first time cost a day on the Alarms fixture.

---

## What is still true, and what the project is actually for

Nothing here invalidates the work. It changes the claim.

**Still true and measured:**

- WinAppDriver is archived and closed-source. Nothing filed against it will ever
  be fixed, including #1079.
- It scores 112/290 on its own compatibility suite on Windows 11.
- A find takes roughly 1070 ms through WinAppDriver versus roughly 33 ms here
  (unmatched conditions — see `LIMITATIONS.md` — but a 30x gap is not noise).
- Its command line, error envelopes and locator semantics are now measured rather
  than guessed, which no other replacement has done.

**The honest value proposition:** an open-source, maintained, considerably faster
implementation of a protocol whose only server was abandoned — with behaviour
verified against recordings of the original rather than reimplemented from a
specification the original did not follow.

**What it should not claim:** that it fixes #857. It does not.

**What it CAN claim, with field evidence rather than inference —
see `docs/CLICK-SEMANTICS.md`:** that clicking works on elements WinAppDriver
cannot reliably click. That defect is documented, reproduced, and measured: a
real MAUI suite went from firing clicks into the taskbar and silently failing on
CI, to 83/83 at CI's exact geometry, purely by replacing coordinate clicks with a
UIA pattern ladder.

The application also had to work around the driver by adding transparent `Button`
overlays to its own markup, and by dropping to FlaUI where WinAppDriver could not
reach. FlaUI wraps the same `IUIAutomation` API this driver uses, so the
capability was always in UIA — WinAppDriver simply was not reaching for it.

That is a better claim than the two it replaces, because it is demonstrable.

**What it may be able to claim, once XPath exists:** that `FindElement` and
`FindElements` agree, which #1079 says WinAppDriver's do not. That becomes a
testable experiment with an obvious control the moment XPath lands — run the same
expression through both endpoints, on both drivers.

---

## The methodological point

The architecture was chosen for a reason that turned out to be unsupported, and
it survives anyway: querying live is still the right default, still cheaper here,
and still avoids a class of staleness. But it was adopted on a hypothesis nobody
checked, and repeated confidently in every document in this repository for two
weeks.

Reading two web pages would have caught it at any point.

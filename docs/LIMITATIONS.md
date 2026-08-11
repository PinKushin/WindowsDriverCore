# Known Limitations

Living document. Every entry is something measured or deliberately chosen, not a
suspicion. Anything fixed gets deleted, not ticked.

Three kinds of entry, kept apart because they need different responses:

- **Not implemented** — work that has not happened. Fix by doing it.
- **Platform constraints** — the OS or UIA behaves this way. Fix by working
  around it, if at all.
- **Deliberate divergences and risks** — decisions taken with a reason, or gaps
  in what the tests can actually prove.

---

## The nine that move are one cluster with one cause, and six of them are mislabelled

The nine tests that separated 133 from 124 are not a noise band. Comparing the
133 run against both 124 runs, **the 133 run is a strict superset** — it passed
everything they passed plus exactly these nine, and nothing ever went the other
way:

```
ClearElement                                 ElementClear.cs
GetElementText                               ElementText.cs
FindElements_ByName                          Elements.cs
ClearElementError_StaleElement               ElementClear.cs
ClickElementError_StaleElement               ElementClick.cs
GetElementAttributeError_StaleElement        ElementAttribute.cs
GetElementDisplayedStateError_StaleElement   ElementDisplayed.cs
GetElementSelectedStateError_StaleElement    ElementSelected.cs
GetElementTextError_StaleElement             ElementText.cs
```

**All nine derive from `AlarmClockBase`, and all nine need the add-alarm page
open.** Random variation does not select a family.

### Six of them never reach the behaviour their name describes

This is the part worth carrying forward, because it would misdirect any triage
that trusts the test name. The stale-element tests are written like this:

```csharp
try { GetStaleElement().Clear(); Assert.Fail("Exception should have been thrown"); }
catch (InvalidOperationException e) { Assert.AreEqual(ErrorStrings.StaleElementReference, e.Message); }
```

`GetStaleElement()` clicks `AddAlarmButton` and then looks for `AlarmSaveButton`.
When that find fails, it throws `InvalidOperationException` **from inside the
helper** — and the catch block, which exists to inspect the driver's stale-element
message, catches it instead and compares the wrong exception. The failure reads:

```
Assert.AreEqual failed.
  Expected:<An element command failed because the referenced element is no longer attached to the DOM.>
  Actual:  <An element could not be located on the page using the given search parameters.>
```

That looks exactly like a driver returning error 7 where error 10 was required.
**It is not.** No stale element was ever produced; the setup failed and the
assertion is reporting on the setup. Fixing our stale-element fault mapping in
response to this message would have been effort spent on a correct code path —
and the earlier prediction table already records two fault-classification fixes
that gained zero, which is the same trap.

The general form: **a `catch` that asserts on a message will happily report a
setup failure as a behavioural one.** Read the stack, not the assertion.

### What is actually broken

One thing, worth nine tests: after our click on `AddAlarmButton`, the add-alarm
page's controls are not findable — `AlarmNameTextBox` for `ClearElement`,
`AlarmSaveButton` for the six, and `GetElementText`/`FindElements_ByName` the
same way. WinAppDriver passes all nine on this machine, so the page does open for
it.

**Answered:** neither. `AddAlarmButton` was *disabled* — the app had hit its
alarm cap — so `Invoke` threw and the click ladder toggled the command bar
instead, reporting success. See "SOLVED: the score is controlled by the alarm
store and app warmth" below for the measurement and the fix.

## The suite DLL in the VM is not the pristine suite

`CLAUDE.md` says the compatibility suite is kept pristine, and the *source tree*
is. The **built DLL in the guest is not**: it contains `FindAlarmNameTextBox` and
`FindAlarmSaveButton`, which exist only in the sibling repo's stash
(`win11 drift accommodations`). Verified by string-scanning
`C:aseline\WebDriverAPI\WebDriverAPI.dll`.

This does **not** invalidate any comparison made today. The DLL was written at
`06:09:07Z`; WinAppDriver 1.2.1's run finished `06:22:24Z`, the 1.2.99 RC's at
`08:56:07Z`, and every one of our runs later still. **All of today's numbers —
281, 264, 133, 124–125 — were measured against this same build.**

What it does mean is that "pristine" describes the checkout, not the artifact
under measurement, and a future session should not assume the two match. On
Windows 10 the accommodations are fallbacks that try the Windows 11 control id
first and fall back to the Windows 10 one, so they cost time rather than change
outcomes — but that is reasoning, not a measurement, and the honest way to close
it is to build the pristine tree and re-run.

---

## Projection beats a live navigator, and the cache request wins at five properties

Measured 2026-08-10, Calculator, 65 nodes, 10 repeats:

```
BULK  FindAll, no properties   : 13.86 ms
PROJ  FindAllBuildCache + 5    : 27.20 ms   <- projection, cached
PROJ  FindAll + 5 live         : 47.92 ms   <- projection, live
NAV   step every node          : 32.31 ms   <- navigator, before reading anything
NAV   step + 5 live per node   : 68.44 ms
NAV   step + cached per node   : 44.44 ms
stepping vs bulk walk          :  2.33x
cache vs live, 5 properties    :  1.76x
```

**The cache request wins at five properties and loses at one.** Both earlier
claims in this document were wrong in opposite directions — "the big lever" and
then "it does not help". The crossover is somewhere between one and five
properties, and building a projection is firmly on the winning side. Neither
claim should have been generalised from a single-property measurement.

**Per-node stepping costs 2.33x the bulk walk before reading a single property.**
So an `XPathNavigator` over live elements — which is what would make staleness
structurally impossible — loses to a projection for whole-tree work, 44.44 ms
against 27.20 ms.

The navigator's advantage was never whole-tree work; it is *not visiting*.
`/Window/List/ListItem[1]` touches a handful of nodes where a projection always
builds all 65. But the expression the compatibility suite actually issues is
`//ListItem[starts-with(@Name, "…")]`, which scans regardless, so the projection
wins the case that matters.

### The design, now measured rather than argued

1. **Parse first, then project only what is needed** — the minimal subtree (an
   absolute path does not need the whole tree) and the minimal property set
   (`//ListItem[@Name…]` needs Name and ControlType, not five).
2. **One `FindAllBuildCache`** with exactly those properties.
3. Build the XML over that cached snapshot **inside the single request**, and
   evaluate with `System.Xml.XPath` so semantics match Microsoft's exactly.
4. **Runtime ids for the matches only.** RuntimeId cannot be cached, so it costs a
   crossing per element — but only the matched set needs one, not all 65.

Step-wise `TreeScope_Children` evaluation stays a later optimisation for paths
without a leading `//`, where it provably cannot change the result.

## WinAppDriver's XPath is essentially COMPLETE XPath 1.0, and that is a warning

Recorded 2026-08-10 against WinAppDriver 1.2.1 driving Calculator, 38
expressions — `Recordings/winappdriver-xpath-surface.json`. Everything worked:

```
axes        self, parent, ancestor, child, descendant,
            following-sibling, preceding-sibling, ..
node tests  //*, node(), named, wildcard steps, absolute paths
predicates  [1] last() position()>1 @a="v" @a not() and or nested
functions   starts-with contains string-length normalize-space
            substring translate concat count
other       union |
errors      malformed -> 500, unknown function -> 500
```

**The tell that it is not hand-rolled:**

```
//Button[1]     matched 8
(//Button)[1]   matched 1
```

Eight is **correct XPath 1.0**. `//Button[1]` means "every Button that is first
among its siblings", not "the first Button in the document". Hand-written
evaluators nearly always get this wrong, and nobody implements it correctly by
accident.

So the likely implementation is: build an XML document of the UIA tree, hand it
to `System.Xml.XPath`. **One theory explaining all three complaints** — complete
semantics because it is .NET's engine, slow because the document is rebuilt per
query, and flaky because that document is a *snapshot* that drifts from the live
tree, which is issue #1079 exactly.

### What this means for us, and it cuts against the plan above

The step-wise live-traversal design is faster and cannot go stale, but it makes us
**reimplement XPath 1.0 semantics by hand** — including the per-parent positional
rule above. Getting that subtly wrong breaks suites that currently pass, and the
failure would look like a flaky driver rather than a wrong evaluator.

The honest middle path is a per-request XML projection:

- build the projection **inside one request**, never cached across calls, so the
  #1079 staleness (which is about holding it *between* calls) cannot happen
- evaluate with `System.Xml.XPath`, so semantics match Microsoft's exactly and
  for free
- keep a node → `IUIAutomationElement` map to turn results back into element ids
- cost is one tree walk per query, which the measurements above show is
  unavoidable for any expression containing `//` anyway

Step-wise `TreeScope_Children` evaluation then becomes an **optimisation for
paths that do not start with `//`**, applied later and only where it provably
cannot change the result — rather than the foundation, where a semantic slip is
indistinguishable from flakiness.

## XPath must not be built on FindAll — measured, and the gap is 16x

Same subject (Calculator), 20 repeats:

```
descendants, TrueCondition  : 12.92 ms  (65 elements)
descendants, Button cond    : 12.53 ms  (47 elements)   <- narrowing saves 3%
children only, TrueCondition:  1.50 ms  (2 elements)    <- 8.6x cheaper
FindFirst Button (early out):  0.79 ms                  <- 16x cheaper
TreeWalker to first Button  :  1.40 ms
```

**Traversal dominates, not marshalling.** Cutting the result set from 65 elements
to 47 saved **3%** — the provider walks the whole subtree either way. So pushing
a control type into the condition, which is the obvious optimisation, is worth
almost nothing on its own.

The two things that ARE worth something:

- **`TreeScope_Children` is 8.6x cheaper than `TreeScope_Descendants`.** A path
  evaluated step by step costs a few cheap hops rather than one expensive walk.
- **Early exit is 16x cheaper.** `FindFirst` genuinely short-circuits, and a
  manual `TreeWalker` that stops at the first match costs 1.40 ms against 12.53.

### So the design is settled by measurement

1. Parse the expression into steps.
2. Evaluate each step with **`TreeScope_Children`** against the current node set.
3. Only a leading `//` needs a descendant pass, and even that walks with
   `IUIAutomationTreeWalker` evaluating the predicate per node, so a single-element
   query stops at the first match instead of completing the walk.
4. Predicates UIA can express (control type, exact property equality) go into
   that step's condition. `starts-with`, `contains` and positional predicates are
   evaluated locally on the live node — UIA conditions cannot express them, so the
   split is forced regardless.

**This also answers the flakiness**, which is the complaint about WinAppDriver's
XPath rather than its speed. Walking live nodes per evaluation means there is no
tree snapshot to drift out of date — the #1079 failure mode this repo already
refuses elsewhere. A snapshot would be faster on paper and wrong in exactly the
way the original was.

## Where a find's time actually goes — and the cache request does NOT help

Measured 2026-08-10 against Calculator (47 buttons), 20 repeats each, through
this driver's own finder and raw `IUIAutomation`:

```
elements matched      : 47
ElementFromHandle     :  0.85 ms
FindAll only          : 12.21 ms
FindAll + 47 ids      : 11.60 ms   <- indistinguishable from FindAll alone
FindAll + names, live : 16.25 ms
FindAll + names, cached: 17.94 ms  <- SLOWER than live
whole driver FindAll  : 14.81 ms
```

**Three predictions, all wrong:**

1. **"A find is 1 + 3N cross-process crossings."** No. Reading `GetRuntimeId` on
   all 47 elements cost *nothing measurable* — the delta against `FindAll` alone
   came out slightly negative, i.e. noise. Whatever `FindAll` returns already
   carries enough that per-element id reads are not paid for individually.
2. **"`IUIAutomationCacheRequest` is the big lever."** It is **0.91x** — nine
   percent *slower*. Building the cache costs more than the reads it replaces at
   this size.
3. **"The layering is what costs."** Our whole find is 14.81 ms against a bare
   `FindAll` of 12.21 ms, so everything this driver adds is ~2.6 ms. **The tree
   walk is the cost**, and it is UIA's, not ours.

**What that means for XPath.** The expensive part is walking the tree, and you
cannot avoid walking it. So the shape is: one `FindAll` with a true condition
(~12 ms), then filter in-process, where the filtering is nearly free. That is
presumably why WinAppDriver's XPath is unremarkable — there is no clever
batching available to be better at.

**Limits of this measurement, stated rather than glossed:** Calculator is a
47-element tree. This says nothing about N in the hundreds or thousands, where a
cache request may well win. It is also a warm provider on a fast desktop, not the
VM. Re-measure before quoting it anywhere else.

Two API facts found the hard way while measuring:

- **`RuntimeId` cannot be cached.** `AddProperty(30000)` is accepted, then
  `GetCachedPropertyValue` throws `E_INVALIDARG`. Same special-casing that makes
  RuntimeId illegal inside a property condition.
- **A cache request's `TreeScope` must include `TreeScope_Element`.** With
  `TreeScope_Descendants` alone the returned elements' *own* properties are never
  cached, and every `GetCachedPropertyValue` throws `E_INVALIDARG` — which reads
  exactly like "that property is not cacheable" and is not.

## Why attaching to the frame keeps failing — the loose fallback grabs an EMPTY frame

**WinAppDriver has no cold/warm difference at all.** Its matched run logged
`app warmed: False` and still scored 281. Ours is 124 cold against 133 warm, so
that 9-test gap is entirely our defect, and the obvious explanation is that
WinAppDriver anchors to a window it never has to abandon — the
`ApplicationFrameWindow`, which survives the rehost that destroys the CoreWindow.

**The frame arrives fast.** Measured, three cold starts:

```
attempt 1: CoreWindow handed at 490 ms; frame findable at 732 ms; gap 242 ms
attempt 2: CoreWindow handed at 490 ms; frame findable at 746 ms; gap 256 ms
attempt 3: CoreWindow handed at 430 ms; frame findable at 645 ms; gap 215 ms
```

So "the frame does not exist yet", which killed three earlier attempts, is a
quarter of a second — worth waiting for, and never previously timed.

**But a fourth attempt failed too, and this is why.** Refusing a bare CoreWindow
so the poll loop waits for its frame does not make the loop wait: it diverts the
search into the **loose "any top-level window that did not exist before" stage**,
which happily returns the frame *before its CoreWindow has been reparented in*:

```
handed 0x00110B8E (ApplicationFrameWindow) at 551 ms
   596 ms  FindFrameWindowHosting -> 0x00000000   handed frame buttons=0
   650 ms  FindFrameWindowHosting -> 0x00000000   handed frame buttons=0
```

The frame-hosting stage correctly reports "not yet" — it matches through the
CoreWindow child and there is not one — while the loose stage hands back the same
frame as an empty shell. Every find against it returns nothing, which is exactly
the "element finds come back empty" that all four attempts produced.

**Attempt five did exactly that — wait for `FindFrameWindowHosting`, and return
zero while a top-level CoreWindow says a frame is coming, so the loose stage is
never reached. It failed too, and differently:**

```
Nothing matched AutomationId 'num5Button' within 30s (last failure: NoSuchWindow)
AResolveThatFinds_Nothing_IsNotCachedAsAFailure:
    expected NotFound but was NoSuchWindow
```

`NoSuchWindow` there means **`ElementFromHandle` could not use the frame** — and
`FindFrameWindowHosting` only matches a frame that already has our CoreWindow as
a Win32 child. A probe at 15 s showed that same frame answering with 50 buttons.

**So a frame has at least three separate readiness stages, and they are not
simultaneous:**

1. the window exists (Win32 `EnumWindows` finds it) — ~250 ms
2. our CoreWindow is its child (`FindFrameWindowHosting` matches)
3. **UIA will resolve it** (`ElementFromHandle` returns a usable element) — later
   still, and this is the one nothing here waits for

Stage 3 is the one that matters and the one no attempt has waited for. It also
cannot be checked from `MainWindowWaiter`: that type lives in `Platform`, which is
Win32-only by design, and UIA lives in `Automation`. Any sixth attempt has to
decide where a UIA-readiness check belongs before writing a line of it — probably
in the launcher's caller, or behind a contract the Platform layer can call
without knowing what UIA is.

**Five attempts, five different causes**, each one disproving the previous
diagnosis rather than confirming it:

| # | believed cause | actual outcome |
|---|---|---|
| 1 | frame lookup ordered too late | returned 0, poll ran to deadline |
| 2 | CoreWindow must be refused | same, and the loose stage grabbed an empty frame |
| 3 | `GA_ROOT` gives the frame | `GA_ROOT` of a CoreWindow is itself |
| 4 | the frame is a superset, prefer the hosted CoreWindow | recovered none of the 19, lost 2 |
| 5 | wait for frame-hosting, skip the loose stage | UIA will not resolve the frame yet |

**Stop and come back to it.** The record above is worth more than a sixth swing
at the end of a session, and every attempt so far has cost a build, a run and a
revert while teaching exactly one new fact.



## Cold-start verification: 118 -> 124, and the two fixes measured

Same conditions as the 118 run — store reset, **cold start on purpose** — at
`05768de`, so only the code changed.

```
run time   30+ min, never finished  ->  3m 25s
result     {"status":"ok","exitCode":0}
score      118 -> 124
```

**Gained 25:**

- all 16 `ActionsError_*` — the session re-resolve
- **5 XPath tests** — `FindElements_ByXPath`, `FindElementError_NoSuchElementByXPath`,
  `FindElementsByNonExistent_XPath`, and both nested forms
- `FindElementError_NoSuchWindow`, `SwitchWindowsError_NoSuchWindow`,
  `CreateSessionFromExistingWindowHandleError_StaleWindowHandle` — the correct
  no-such-window fault instead of "no such element"
- `GetWindowSize`

**Lost 19**, a coherent cluster: `FindElement_ByClassName`, `FindElements_ByName`,
all four `FindNestedElement*`, `GetElementText`, `GetElementAttribute`,
`GetElementEnabledState`, `GetElementDisplayedState`, and several
`*Error_StaleElement`. All had passed cold **before** these changes, and they are
the same set still missing on cold versus warm.

**Attributed by an isolation run, and the suspicion was wrong.** A build with the
re-resolve kept and the fail-fast disabled (`bd9479e`) scored **122** under
identical conditions:

```
                    gained  lost   net
neither fix                        118
re-resolve alone      23     19     +4   -> 122
fail-fast on top       2      0     +2   -> 124
```

**The fail-fast is pure gain.** It adds `FindElementError_NoSuchWindow` and
`GetWindowSize` and costs nothing. I had written down that it was probably too
aggressive on cold start; it is not, and only the isolation run says so.

**All 19 losses belong to the re-resolve**, and the set names the mechanism:
five `*Error_StaleElement` tests, `FindElement_ByClassName`, `FindElements_ByName`,
all four `FindNestedElement*`, `GetElementAttribute`, `GetElementDisplayedState`,
`GetElementEnabledState`, `MiscellaneousSession_MultiSessionsSingleInstance` —
tests that assert a specific **count** or a specific **first match**.

That is what moving the search root does. Re-resolving points the session at the
`ApplicationFrameWindow` where it began at the `Windows.UI.Core.CoreWindow`, and
the frame is a **superset** — measured earlier at 73 descendants against the
CoreWindow's 65, because it also contains the title bar and window chrome. Counts
change, first matches change, and tests asserting either of those break while
everything else keeps working.

**Measured, and the frame theory was WRONG.** The measurement confirmed the
mechanism it was looking for and the fix built on it still failed:

```
handed at launch : 0x02030620  Windows.UI.Core.CoreWindow
re-resolved to   : 0x01EA0702  ApplicationFrameWindow
hosted CoreWindow: 0x02030620  <- the same handle the session began with
frame              Buttons=50
hosted CoreWindow  Buttons=47   <- three fewer: Minimize, Maximize, Close
```

So the frame *is* a superset, exactly as predicted — and preferring the hosted
CoreWindow **recovered none of the 19** and lost two more
(`FindElementError_NoSuchWindow`, `GetWindowSize`), scoring 122 against 124.
Reverted on that measurement. **A confirmed mechanism is not a confirmed cause.**

| build | score |
|---|---|
| neither fix | 118 |
| re-resolve to frame, no fail-fast | 122 |
| re-resolve to frame + fail-fast | **124** |
| re-resolve to CoreWindow + fail-fast | 122 |

**What the lost 19 actually have in common** — re-read after the frame theory
died — is not counts. It is **element ids issued earlier in the session**:
`GetElementAttribute`, `GetElementText`, `GetElementDisplayedState`,
`GetElementEnabledState`, all four `FindNestedElement*` (which search *within* a
previously found element), `CompareElementsError_NoSuchElement`, and five
`*Error_StaleElement`. Every one of them hands back an element id and then uses
it.

**Next hypothesis, untested:** re-pointing the session at a different window
invalidates ids already issued against the old one, because an element id is a
runtime id resolved within a window scope. That would break exactly the tests
that hold an id across the re-resolve and nothing else.

**Do not act on that until it is measured** — the frame theory looked at least as
good and cost a build, a run and a revert.

Incidental, and worth keeping: on the Windows 11 host the original CoreWindow is
**reparented and stays alive** (same handle, `IsWindow` true); in the Windows 10
guest it is **destroyed**. That is why the whole defect only ever appeared in the
guest, and why three attach-time fixes looked fine on the host.

## Typing: one bug fixed, one still open — WM_NULL does not drain input

**FIXED, and confirmed.** `Ctrl+A` was typing a literal `a`.
`KEYEVENTF_UNICODE` injects a character directly and bypasses the keyboard
layout, so modifier state does not combine with it. The suite clears its edit box
with `Ctrl+A` then `Delete`, so every clear *appended* an `a` — measured, the
residue grew by exactly one per test, `<a>` through `<aaaaaaaaaaaa>`. Under a
held modifier the character now goes through `VkKeyScan` as a virtual key, and
the accumulation is gone.

**STILL OPEN: the client can read a control before the keystrokes land.**

Measured by reading our `/text` and the control's own `WM_GETTEXT` back to back,
in that order:

```
round 1  ours=6   truth=8
round 2  ours=28  truth=30
round 3  ours=47  truth=54
```

`truth` is read *second* and always sees more. Characters are still arriving
between two adjacent reads, long after `POST /keys` has answered.

**`WM_NULL` cannot fix this, which is the part worth remembering.** A synchronous
`SendMessage` is delivered ahead of queued input — Windows processes sent
messages before posted messages and input — so it jumps the queue, returns
immediately, and proves only that the thread is responsive. Two attempts were
built on it:

| attempt | result |
|---|---|
| wait on the session window | no change; `Actual:<ab>` |
| wait on the focused window (`GetGUIThreadInfo`) | no change; `Actual:<ab>` |

The second was aimed at a real problem that does not apply here anyway: this
fixture drives **Notepad**, a classic Win32 application whose window and thread
are its own. The `ApplicationFrameWindow`/`ApplicationFrameHost` split it
addressed is a *packaged* app concern.

**What is NOT the problem**, all measured: typing itself (52 of 52 characters
arrive), our `/text` route (agrees with `WM_GETTEXT` exactly when nothing is in
flight), and input loss (nothing is lost — it is late).

**Where to look next.** WinAppDriver passes these tests, so it either types
slowly enough that the application keeps up, or it waits on something that
actually reflects the input queue. Candidates, none tried: sending per-character
batches so each `SendInput` yields; `AttachThreadInput` and then reading the
input-queue state; or waiting until the control's own value stops changing, which
is a poll on the condition rather than a sleep. Measure WinAppDriver's `/keys`
response time first — if it is far slower than our 14 ms, that alone is the
answer.

## Cold start: the session's window handle must be re-resolvable, not fixed

**Root cause found 2026-08-10, after three wrong fixes.** Probed through this
driver's own finder, at the moment the waiter returns:

```
waiter handed : 0x00210CE6  class=Windows.UI.Core.CoreWindow
GA_ROOT of it : 0x00210CE6  class=Windows.UI.Core.CoreWindow   <- itself
finds from it : num5Button=1  buttons=47                       <- all fine
```

**There is no frame yet.** The CoreWindow is top-level and is its own root, and
finds from it work perfectly. Every "attach to the frame instead" fix therefore
asks for something that does not exist at that instant:

| Attempt | Result |
|---|---|
| prefer `FindFrameWindowHosting` first | returns 0, poll runs to deadline, caller gets window 0 |
| reject a CoreWindow and wait for its frame | same, one test took 30 s — a timeout, not a slow find |
| resolve via `GetAncestor(GA_ROOT)` | `GA_ROOT` is the CoreWindow itself, so also 0 |

All three looked like "empty element finds". None of them were about the frame
being a bad search root — measured, a frame is a superset of its CoreWindow
(73 descendants against 65).

**What actually happens:** the frame appears *later*, when the application is
rehosted, and in the Windows 10 guest the original CoreWindow is **destroyed**
rather than reparented. That destruction is the "Currently selected window has
been closed" that kills every `ActionsError_*` test at `TestInit`, long after
session creation and nowhere near it.

**So the fix is not at attach time.** The session has to be able to *re-resolve*
its window when the handle it holds dies, rather than treating the first window
as immutable for the session's life. That also makes `/window_handle` report the
frame, which is what WinAppDriver reports.

`APackagedApplicationsWindow_IsNeverTheHostedCoreWindow` is `[Ignore]`d and is
the specification for it. `GetAncestor`/`GA_ROOT` remain in `Win32` because a
re-resolve will want them once a frame does exist.

### Superseded: we attach to the CoreWindow, not the frame window

Measured 2026-08-10, three cold starts through our own driver:

```
session window_handle=0x0025073E  class='Windows.UI.Core.CoreWindow'  title='Alarms & Clock'
   +0s  IsWindow=True visible=True   find AddAlarmButton -> status=7
   +2s  IsWindow=True visible=True   find AddAlarmButton -> status=0
  real top-level window: hwnd=0x00170B7A class=ApplicationFrameWindow name='Alarms  Clock'
```

**The handle is not dead** — the hypothesis that a transient window was adopted
and then closed is wrong. Two separate defects instead:

1. **We attach to the wrong window.** `MainWindowWaiter` tries "a visible window
   owned by the launched process" *before* "the frame window hosting it". For a
   packaged application the `Windows.UI.Core.CoreWindow` is owned by the app and
   the `ApplicationFrameWindow` is owned by `ApplicationFrameHost`, so the check
   that looks the most precise is the one that picks the wrong window. A
   CoreWindow is destroyed and recreated when the app is rehosted or resumed; the
   frame window persists. That is what later surfaces as "Currently selected
   window has been closed", which is why it appears on a long run and not on a
   short probe.

2. **The tree is not ready at +0s.** Two of three cold starts could not find
   `AddAlarmButton` immediately, and all three could by +2s. A session created
   the instant a window appears hands back a window whose content has not
   arrived. Our finds are ~33 ms, so we reach it inside the gap that a ~1070 ms
   driver never sees.

## SOLVED: the score is controlled by the alarm store and app warmth

Four explanations were published for one moving number and **all four were
wrong**. The cause is environmental, completely determinate, and reproduces
test-for-test.

**Controlled, all on commit `21a18dd`, one variable at a time:**

| Alarm store | Alarms & Clock | Score |
|---|---|---|
| capped (~162 alarms) | warm | 124 |
| clean | **cold** | 118 |
| clean | warm | **133** |

Against the capped run, the clean+warm run **gained exactly the nine and lost
nothing**. Against run 17 — a different commit, `6956530` — the clean+warm run
passes an **identical set of 133 tests**, not merely the same count.

So 133 was never an anomaly. It was the only earlier run that happened to have a
warm app and a store not yet at the cap.

### 1. WE break the suite's cleanup, so alarms accumulate until "Add new alarm" dies

**Corrected — the earlier claim here was wrong and blamed the wrong party.** It
said the suite "creates alarms faster than it deletes them". It does not. It
deletes them perfectly: WinAppDriver runs the whole suite from a fresh store and
leaves **exactly the one default alarm**. Under our driver ~162 pile up.

`DeletePreviouslyCreatedAlarmEntry` is:

```csharp
while (true) {
    try {
        var e = session.FindElementByXPath($"//ListItem[starts-with(@Name, \"{name}\")]");
        session.Mouse.ContextClick(e.Coordinates);
        session.FindElementByName("Delete").Click();
    } catch { break; }
}
```

Measured against our driver:

```
xpath //ListItem[starts-with(@Name,"Good morning")]   -> status 19  Invalid XPath expression
name  'Alarm'                                         -> status 0   (reachable another way)
POST /moveto /click /buttondown /buttonup /doubleclick -> status 9   Command not recognized
```

It fails on the **first** call, the bare `catch` swallows it, and nothing is ever
deleted. Two unimplemented features — XPath and the mouse command family — with a
blast radius far outside their own tests: they silently disable the suite's
cleanup, which then degrades every later run.

Once those land, the accumulation stops and the reset below becomes unnecessary.

With enough alarms the app disables **only** `AddAlarmButton`:

```
AddAlarmButton                  enabled=False   <- only this one
SelectAlarmsButton              enabled=True
MoreButton                      enabled=True
AlarmCollectionPageCommandBar   enabled=True
```

Nothing looks broken, and the nine tests that need that button all fail.

**There is a real limit, and it sits near 162.** Filling the list until the
button refuses *is* how a limit gets measured, and this one was reached the slow
way: ~162 alarms disables it, 1 alarm enables it, and only that one button is
affected — which is what an application enforcing a maximum looks like. The
figure is an estimate from the virtualized list (`VerticalViewSize` 17.33% with
28 items realized, so 100/17.33 × 28 ≈ 162), not an exact integer, and no
Microsoft documentation for it was found. **Pin it exactly** once alarm creation
works: add alarms one at a time and record the count at which `AddAlarmButton`
goes disabled.

The alarms are **not** in `LocalState` — clearing that changes nothing, tried first.
They live in the UWP settings hive,
`%LOCALAPPDATA%\Packages\Microsoft.WindowsAlarms_8wekyb3d8bbwe\Settings\settings.dat`.

### 2. Resetting the store cold-starts the app, which costs sixteen more

The first reset run scored **118**, *below* the 124 it replaced, despite gaining
the nine. All sixteen `ActionsError_*` tests flipped to failing:

```
Initialization method WebDriverAPI.Actions.TestInit threw exception.
System.InvalidOperationException: Currently selected window has been closed.
```

Beginning with the **first test of the run** — so nothing killed the window
mid-run; the session never had a usable one. Every earlier run had silently
inherited a warm app left behind by its predecessor.

**So a run must reset the store AND warm the app**, waiting for `AddAlarmButton`
to be *enabled* rather than merely present, since present-but-disabled is the cap
state. `tools/vm/Invoke-CompatibilitySuite.ps1` does both and pins the commit.

### 3. What this invalidates, and what it does not

**Every compatibility number taken before this was measured on an uncontrolled
environment**, including the WinAppDriver comparison: 1.2.1 scored 281/290 at
`06:22Z`, before the cap was reached, while our runs came after. The same suite
DLL was used throughout so the comparison is not void, but it is **not matched**,
and needs that caveat wherever it is quoted.

The `/timeouts` +62 and window-reads +28 gains are far outside this effect and
stand. **The +15 credited to Actions validation also stands** — those sixteen
tests all pass in the clean+warm run; they simply need a live window to be
observable at all, which is a fact about the harness rather than about the code.

Note also that `6956530` and `21a18dd` pass an identical set. Everything merged
between them moved the compatibility score by **zero**.

### 4. The driver bug this was hiding

With `AddAlarmButton` disabled, `InvokePattern.Invoke()` threw, the click ladder
fell through to its ancestor walk, reached `AlarmCollectionPageCommandBar` —
which advertises `Toggle` and `ExpandCollapse` — and toggled it. The app bar
opened and closed across three probe attempts (`MoreButton` alternating between
"More app bar" and "Less app bar", an `OverflowPopup` appearing and vanishing)
while the driver answered `status 0`.

Fixed: a disabled element is refused with `NotInteractable` before any rung runs,
before scrolling and before foregrounding, so a refusal has no side effects. The
regression test asserts a **bystander** — the hosting control's `ToggleState` —
because "refused" and "climbed and toggled the parent" both leave the disabled
button untouched.

### 5. The four wrong explanations, kept deliberately

1. "my change regressed it" — wrong, the commits were identical
2. "the suite has a 9-test noise band" — wrong, four repeats spread by one
3. "the guest agent's launcher caused it" — wrong, restoring it changed nothing
4. "it was an anomaly" — wrong, and worst of the four because it *ends* the search

Each was reasoned from a single observation and written up before being tested.
The cause was found by diffing which test **names** moved rather than theorising
about the count, which took one query and should have been the first move. When a
number moves: find out which items moved, and whether they form a family.

## Which predictions have been right, measured across seventeen runs

Worth recording because the pattern is consistent and it should change what gets
worked on next.

**Predicted well — missing routes.** Every large gain came from a command the
driver simply did not answer:

| Change | Predicted | Actual |
|---|---|---|
| `POST /timeouts` | 167 | **+62** |
| window read routes | 35 | **+28** |
| Actions payload validation | 20 | **+15** |
| guarded mouse rung | 9 | **+8** |

**Predicted badly — wrong faults.** Both attempts to fix a command that answered
with the wrong *error* gained nothing at all:

| Change | Predicted | Actual |
|---|---|---|
| find reports window closed | 24 | **0** |
| element commands report window closed | 32 | **0** |

Both are correct by the recorded contract and both are covered by protocol tests
that fail when mutated. They simply are not what those suite tests are blocked
on, and reading the assertion message was not enough to tell — the message says
what the test compared, not what stopped it getting there.

**The lesson for planning:** a failure message naming an error string is weak
evidence about the cause. `Command not recognized` is strong evidence, because
there is only one way to produce it. Rank work by the second kind.

---

## WinAppDriver's newer release is worse: 264 against 1.2.1's 281

**Measured 2026-08-10, same Windows 10 22H2 guest, same WebDriverAPI.dll, same
applications.**

| Build | Passed | Failed | Wall clock | Median per test |
|---|---|---|---|---|
| 1.2.1 (last stable, Nov 2020) | **281/290** | 9 | 10.7 min | 0.46 s |
| 1.2.99 RC ("v1.3 RC1", Jul 2021) | **264/290** | 26 | 11.7 min | 0.42 s |

**17 tests regressed** in the newer build, including `GetStatus`,
`Launch_SystemApp`, `Close_SystemApp`, `CreateSessionWithArguments_SystemApp`,
`Pen_LongClick`, `TouchLongTap` and `SendKeys_NonPrintableKeys`.

**The two time measures disagree, and both are correct.** Median per-test time
improved slightly while wall clock got a minute worse — 17 extra failures means
17 extra error paths and timeouts, which inflate the total while the typical
passing command is marginally quicker. Quoting either alone would mislead.

**This is why 1.2.1 is the baseline.** It is the strongest WinAppDriver
available, not merely the most convenient — and it explains a release history
that otherwise looks odd: v1.2.99 was published as a release candidate in July
2021 and never promoted in four years. It regresses its own compatibility suite.

Name the build in any comparison. "WinAppDriver scores 281" is only true of
1.2.1 on Windows 10.

---

## The published exe will not start unless .NET is where it expects

**Measured 2026-08-10 in the Windows 10 guest.** `dotnet publish` produced
`WindowsDriverCore.exe`, it launched, and it exited immediately — nothing ever
listened on the port and the harness sat in its connect loop looking like a hang.

The cause is the apphost. The guest's SDK was installed by Microsoft's
`dotnet-install.ps1` into `C:\dotnet`, which does **not** register the runtime
globally, so the apphost looks in the default location, finds nothing, and dies.
Running `dotnet WindowsDriverCore.dll` instead works, because that skips apphost
resolution entirely.

**This is a shipping problem, not a test-rig quirk.** A user whose .NET lives
anywhere non-standard gets an executable that starts and vanishes with no
message. WinAppDriver has no equivalent failure — it ships self-contained.

Options, none taken yet: publish self-contained, publish AOT, or detect the
missing runtime and say so instead of exiting silently. The last one is the
cheapest and would turn an invisible failure into a sentence.

**Nothing caught this until an integration test ran the actual executable.**
Every earlier test either drove the automation layer directly or hosted the
pipeline in-process with `WebApplicationFactory`, and none of those run the
apphost, bind a socket, or parse an argument.

---

## The matched comparison: 133/290 against WinAppDriver's 281/290

> **Re-measured 2026-08-10 under the controlled procedure, and the caveat this
> section briefly carried was wrong.** I suspected WinAppDriver's 281 had been
> flattered by running before the alarm cap was reached. Measured: with the store
> freshly reset it scores **281 again**, the same nine failures. Its score does
> not depend on the contamination at all — only ours did. The comparison was fair
> on that axis the whole time.
>
> | Driver | Environment | Score |
> |---|---|---|
> | WinAppDriver 1.2.1 | store reset | **281/290** |
> | WindowsDriverCore `21a18dd` | store reset, app warmed | **133/290** |
>
> WinAppDriver's nine failures are two browser tests (legacy Edge is gone), three
> `TouchClick`/`TouchFlick` class-initialisation failures, `SwitchWindows`,
> `GetWindowHandles_ModernApp`, `CreateSessionWithArguments_ModernApp` and
> `GetLocation` — environmental, not capability.
>
> **The 148-test gap, by class.** This is the honest backlog, in priority order:
>
> | Class | Tests only WinAppDriver passes |
> |---|---|
> | `Session` | 13 |
> | `SendKeys` | 12 |
> | `ElementSendKeys` | 12 |
> | `WindowTransform` | 10 |
> | `ActionsPen` | 9 |
> | `ActionsTouch` | 8 |
> | `Window` | 8 |
> | `ElementElements` | 6 |
> | `ElementElement` | 5 |
> | `Screenshot` | 5 |
> | `Actions` | 5 |
> | `ElementEquals` | 4 |
> | `ElementActive` | 4 |
> | `Elements` | 3 |
>
> `SendKeys` and `ElementSendKeys` together are **24 tests and the single largest
> theme** — and the keyboard itself is proven working (`Wx9!` sent, `Wx9!`
> received), so the defect is in focus or routing rather than in input
> synthesis.

**Measured 2026-08-10, both drivers in the same Windows 10 22H2 guest, same
WebDriverAPI.dll, same applications, same session.** The first like-for-like
either driver has had.

| Driver | Score |
|---|---|
| WinAppDriver 1.2.1 | **281/290** |
| WindowsDriverCore, run 1 | 19/290 |
| WindowsDriverCore, run 2 | 19/290 — packaged-window fix |
| WindowsDriverCore, run 3 | 81/290 — `/timeouts` (**+62**) |
| WindowsDriverCore, run 4 | 81/290 — window-closed fault |
| WindowsDriverCore, run 5 | **109/290** — window routes (**+28**) |

**Two runs scored the same as the one before and neither was wasted.**

Run 2's packaged-window fix took `Could not find main window for application`
from ~120 failures to **zero** — the fixtures simply died one step later, on
`/timeouts`. **The score only moves when the LAST blocker in a chain goes**, so
read the failure distribution, not the total.

Run 4 is the more interesting one. Reporting `NoSuchWindow` from find instead of
`NoSuchElement` was correct and gained nothing, because it converted 22
assertions into `TestInit` explosions with the same count. The suite keeps a
`static` session across every test class sharing a base, so once one test
deliberately closes a window, every later class inherits a session pointing at a
dead handle. WinAppDriver does not have the problem because `session.Quit()`
closes the application and the next `Setup()` starts clean — **this driver does
not implement application shutdown on `DELETE /session`**, so state leaks between
classes. That gap is now the root of the largest remaining cluster.

### What is left, ranked

| Cause | Tests | Kind |
|---|---|---|
| session state after a window closes | 27 | needs `DELETE /session` shutdown |
| `POST /actions` | 26 | missing subsystem |
| window write ops (`POST /window`, `/window/current/size`, `DELETE /window`) | 15 | missing routes |
| `POST /keys` | 12 | missing route |
| stale-element message on element commands | 9 | wrong fault, same class as run 4's |
| long tail | ~57 | one test each, no common cause |

**The long tail is genuinely long.** 57 failures with no shared root means the
cheap structural wins are nearly exhausted; from here the score moves in ones and
twos rather than in sixties.

**19/290 is not 271 missing features.** The failures collapse into a handful of
causes, because a failed `ClassInitialize` takes every test in its class with it:

| Cause | Tests | Kind |
|---|---|---|
| `Could not find main window for application` | ~120 | one launcher defect |
| `POST /session/{id}/timeouts` not recognized | 21 | missing route |
| wrong error-message text | 21 | string constants |
| `POST /session/{id}/keys` not recognized | 12 | missing route |
| `GET /session/{id}/window/current/size` not recognized | 11 | missing route |
| TouchFlick — app not installed | 9 | environment |

**The launcher defect is the whole ballgame.** Fifteen fixtures die in
`ClassInitialize` on it. The suite drives Alarms & Clock, and a packaged
application's window belongs to `ApplicationFrameHost` rather than to the process
that was activated — the same trap that broke the FlaUI benchmark, where matching
a window by process id found nothing. WinAppDriver handles it; this driver does
not, and roughly 120 tests are downstream of that one difference.

**Read the fixture count, not the test count.** WinAppDriver's own 112/290 on
Windows 11 has the same structure — a small number of broken class fixtures
multiplied across their members — which is why it recovers to 281/290 on Windows
10 with nothing changed but the operating system.

---

## Not implemented

| Area | State | Notes |
|---|---|---|
| **Duplicate and unnamed elements** | Two of three answers implemented | `POST /elements` + client index, and **nested find** (`/element/{id}/element`), both serve. XPath positional predicates — `(//Button)[3]` — do not. Synthetic identifiers are deliberately **not** generated: any invented id is only as stable as what it derives from, and every source is either already expressible (tree position *is* positional XPath), already unstable (content), or already non-durable (RuntimeId does not survive a restart). Scope the search instead of inventing a name. |
| **XPath locator** | Every expression reported as `XPath Lookup Error` (status 19) | UIA has no XPath. WinAppDriver evaluates its own over the tree. Reporting valid expressions as invalid is wrong, but wrong *loudly* — silently matching nothing would look like a correct search. |
| ~~**`DELETE /session` app shutdown**~~ | **Implemented 2026-08-10.** Closes the application, but only one this driver LAUNCHED — a desktop session addresses explorer and an attached session addresses somebody else's window, and both have real process ids. | Was worth 27 tests on the compatibility suite. |
| **Implicit wait** | `POST /timeouts` accepts and validates, then ignores | Find does not retry. This is the defect that cost the old implementation 167 tests, so it must not ship unfixed. |
| **Element interaction** | Reads done; writes on the pattern path only | `click`, `clear` and `value` serve through UIA patterns. **No mouse path yet**, so a genuinely pattern-less target answers `element not interactable` rather than falling back to a coordinate click. Deliberate ordering — with a coordinate fallback available it becomes the path of least resistance. |
| **Keyboard input** | Not implemented | `POST /value` sets ValuePattern's value rather than sending keystrokes, so **no key events reach the application**: anything driven by `KeyDown` rather than by the value changing will not fire, and an element that refuses ValuePattern reports `element not interactable` rather than falling back. `/keys` (session-level send-keys) does not exist. |
| **Element-valued attributes** | Render as `null` | `LabeledBy`, `ControllerFor`, `Selection.Selection` and the other properties whose UIA value is an element or element array. What WinAppDriver sends for these was never measured, and inventing a spelling would be a divergence written as if it were a contract. |
| **`/text` on a Selection** | Implemented, unmeasured | The rung that answers a list's selected item comes from WinAppDriver's own `ElementText.GetElementText` (`MinuteLoopingSelector.Text == "00"`), not from a measurement here. No Windows 11 app tried so far still exposes a looping selector in a reachable state. |
| **Issued-element ids grow per distinct element** | Per session, until `DELETE /session` — **measured, and smaller than it reads** | `ElementRegistry` keeps one short string per element ever returned, which is what makes stale (status 10) distinguishable from unknown (status 7). Measured 2026-08-09 through the HTTP surface: 2000 distinct elements cost 2000 records, but **2000 finds of the same element cost one**. Growth follows distinct elements, not commands, so a page-object suite hammering the same controls costs a constant. `DELETE /session` releases all of it. Not worth bounding until a suite exists that finds a hundred thousand genuinely different elements in one session. |
| **Window routes** | Not started | `window/size`, `window/{handle}/size`, `/position`, `/maximize`, `window_handle(s)`, switch, close. |
| **Mouse, touch, Actions** | Not started | `buttondown`/`buttonup`/`click`/`doubleclick`/`moveto`, eight `touch/*`. ~20 of the 70-test backlog is Actions **error validation** only, which needs no Actions implementation. |
| **Screenshots** | Not started | `/screenshot` for session and element. |
| **`/source`** | Not started | |
| **UWP `appArguments`** | Passed to activation, never verified | No test covers whether a packaged app receives them. |

---

## Known performance defects

**MEASURED AND WRONG — the N+1 is not a performance problem.** This entry
claimed the per-match `GetRuntimeId()` calls were expensive cross-process round
trips and would be "the large win". Measured against Calculator, 47 matches:

| | run 1 | run 2 |
|---|---|---|
| search (`FindAll`) | 17.55 ms | 11.33 ms |
| reading 47 runtime ids | 0.22 ms | 0.14 ms |
| id reads as share of total | **1%** | **1%** |

About 4.7us per id. UIA serves them from the element proxy rather than making a
call per element. A cache request would save roughly 1%, so it is not worth doing
for performance.

**The search itself is ~80% of the cost**, and that is UIA walking the tree — not
something this project can make faster except by searching less of it.

**The managed share cannot be pinned down with this instrument.** The two runs
above report 17% and 29% for managed overhead, and the COM-only path moved by
5.5 ms between them despite no change to that code. Run-to-run variance is larger
than the effect. Wall-clock timing of a cross-process call on a live desktop is
not precise enough; this belongs in `bench/` under BenchmarkDotNet with warmup
and reported variance.

**What this settles anyway:** a native shim can only address the managed share,
which is at most 17-29% and unmeasured within that. Not 30x. The language
question is closed on those grounds — see `PROJECT-KNOWLEDGE.md`.

`IUIAutomationCacheRequest` remains the right tool the moment a find needs
*several* properties per element rather than one — name, class and control type
together would otherwise be three calls each. It is simply not the win it was
predicted to be for runtime ids alone.

**FIXED — resolution no longer walks the tree on every element command.**
`CachingElementResolver` keeps the live element it resolved, keyed by window and
id, and verifies the handle's runtime id on every use before trusting it.

| Property read, num5Button, 20 samples | |
|---|---|
| Walking the tree per command | 19.40 ms |
| Cached handle | 0.45 ms |
| | **43.5x** |

It is an optimisation and nothing more: a miss, an eviction, or a handle whose
identity check fails all fall through to the walk, which is correct on its own.
Bounded at 256 handles with LRU eviction, because each one keeps a provider
object alive inside the application under test, and released on `DELETE /session`.

The reason this was not done sooner was a rule against holding elements between
calls, which `HeldElementLivenessTests` refuted — see `PROJECT-KNOWLEDGE.md` §0.
Remaining cost is the initial find, which is a genuine search.

## Measured against FlaUI, 2026-08-09 — we are not at the floor

In-process, both rooted at the same HWND, same Calculator, BenchmarkDotNet.
Three clean runs.

| | ours | FlaUI | |
|---|---|---|---|
| find by automation id | 11.7 / 11.8 / 16.5 ms | 8.6 / 8.4 / 8.3 ms | **FlaUI ~1.4x faster** (ratio 0.737, 0.716) |
| read one property | 339 / 346 / 364 us | 116 / 113 / 139 us | **FlaUI ~3x faster** |
| allocation, find | 336 B | 918 B | we allocate 2.7x less |
| allocation, read | 177 B | 32 B | we allocate 5.5x more |

**The prediction written into the benchmark before running it was wrong.** It
said find should come out roughly equal because both are dominated by the same
cross-process tree walk. FlaUI is consistently faster, and the ratio is stable
across runs, so it is not noise.

**TESTED, and it was most of the gap.** `POST /element` now calls `FindFirst`.
The decomposition, in-process, same HWND:

| | before | after |
|---|---|---|
| ours, singular find | 11.9 ms | **10.4 ms** |
| raw COM `FindAll`, no id reads | 12.0 ms | 12.3 ms |
| raw COM `FindFirst` | 9.8 ms | 10.2 ms |
| FlaUI | 9.0 ms | 9.3 ms |

Two things fall out. **Our layer costs about 2%** — the finder is within noise of
raw `FindFirst`, so there is nothing to optimise in the wrapper. And **the
remaining ~10% is inside the UIA call itself**, since raw `FindFirst` is still
slower than FlaUI's `FindFirstDescendant`. That is not our code, and the next
hypothesis is that FlaUI searches a narrower tree view or uses a cache request.
Untested.

`FindAll` stays for `POST /elements`, which cannot stop early, and for search by
element id, which must enumerate and compare because UIA rejects RuntimeId in a
property condition.

**The superseded candidate, kept because it was wrong in an instructive way:** `POST /element` calls `FindAll` and then
reads `GetRuntimeId` for every match, when the route uses only the first.
FlaUI's `FindFirstDescendant` stops at the first hit. On a locator matching one
element the two should be close, so this predicts the gap widens with match
count — which is a testable claim rather than an explanation.

**The read gap has a clearer cause and is the same shape.** Reading `/text`
through this driver costs a runtime-id validation on the cached handle, a
ValuePattern availability check, possibly a Selection availability check, and
then the Name — four cross-process calls against FlaUI's one. Round trips, as
predicted; `IUIAutomationCacheRequest` is the documented answer.

**What the first run got wrong, and how it was caught.** It reported this driver
23x *faster* at reading. The FlaUI case called
`FindFirstDescendant(...).Name` — re-finding the element every iteration, so
8.9 ms was a find rather than a read, against a method named "read a property
from a held element". The benchmark's own doc comment carried the prediction that
*if this driver came out much faster, suspect the benchmark before believing it*,
which is what caught it. Corrected to hold the element from setup.

**The remaining performance work, in order:**

1. ~~Move the measurement into `bench/` under BenchmarkDotNet.~~ Done, and it
   settled the question the wall-clock instrument could not: see above.
2. Benchmark FlaUI in-process for the floor, and WinAppDriver over HTTP for the
   baseline, on matched operations.
3. Attack the search cost — narrower tree scope, more selective conditions —
   since that is ~80% of it.
4. A native shim only if the managed share turns out to be real and large, which
   the evidence so far does not suggest.

## Platform constraints

**RuntimeId cannot be used in a UIA property condition.**
`CreatePropertyCondition(UIA_RuntimeIdPropertyId, int[])` fails with
`E_INVALIDARG`. Found by trying it; not documented anywhere obvious. A search by
element id therefore enumerates all descendants and compares, which costs a full
tree walk. The alternative — caching elements between calls — is exactly the
design that produces #857 and #1079, so the cost is accepted.

**A packaged application's window is not owned by the process that was
activated.** `IApplicationActivationManager.ActivateApplication` returns a broker
process id; the window belongs to `ApplicationFrameHost`. The launcher resolves
the owning process from the window rather than trusting the returned id. An
assertion of "process id is non-zero" would pass on the broker and hide this.

**`ActivateApplication` reports an unknown package as `E_INVALIDARG`.**
Through `Marshal.ThrowExceptionForHR` that surfaces as `ArgumentException`, not
`COMException` — so catching by exception type silently misses it. The HRESULT is
now read directly. This is also the origin of the old implementation's
unexplained `"Value does not fall within the expected range"`: it is
`E_INVALIDARG`'s stock message.

**Windows 11 `notepad.exe` is a launcher stub.** It starts the packaged Notepad
and exits, so the launched process owns no window. Notepad is WinUI 3, so the
window is not an `ApplicationFrameWindow` either. Both obvious lookup strategies
miss it; the search falls back to "a top-level window that did not exist before
the launch".

**Native AOT is unavailable.** `Marshal.ReleaseComObject` and built-in COM
interop are unsupported under AOT. Reaching it would mean rewriting the COM layer
on `ComWrappers`. Not planned — see `docs/PROJECT-KNOWLEDGE.md` for why the
performance argument does not motivate it either.

**`LibraryImport` cannot marshal two of our signatures.** `GetClassName`'s
`[Out] char[]` needs runtime marshalling disabled assembly-wide (SYSLIB1051), and
`PROCESSENTRY32W`'s fixed-size inline string is unsupported. Both stay on
`DllImport`, each with the reason at the declaration.

---

## The arrangement production suites actually use

**One session for a whole suite, not one per fixture.** That is what the Appium
documentation shows and what production suites tend to follow: a single
`POST /session`, hundreds or thousands of commands, one `DELETE` at the end.

This driver's own integration fixtures do the opposite — one session each, via
`[OneTimeSetUp]`, matching WinAppDriver's own `[ClassInitialize]` style. That is
reasonable for testing *this* code, and it means **nothing in the suite exercised
what a session accumulates over its life** until `LongLivedSessionTests` was
written. Two pieces of per-session state grow: the issued-element record and the
resolver's handle cache. Both had been written down here as untested risks.

Measured now for the registry, headless, through the HTTP surface. Still
untested for the handle cache: `CachingElementResolver` evicts at 256 handles and
nothing has ever driven it past that, because every fixture gets a fresh
resolver. That eviction path is live code with no coverage, and the recommended
arrangement is precisely the one that reaches it.

---

## Tooling that is configured but does not run

**Stryker.NET could not analyse this solution, and the cause was ours.**
Fixed 2026-08-09 after being wrong about it twice.

The first diagnosis blamed the `.slnx` solution format. That was a guess, and the
evidence already contradicted it: a run from the test project directory, with no
solution involved at all, failed the same way.

Bisected against a minimal project instead. Stryker mutates a clean
`net10.0-windows` project fine, with central package management, with no
solution file. It breaks the moment `TargetFramework` is declared **only** in
`Directory.Build.props`.

**Buildalyzer, which Stryker uses to discover projects, reads the project file
textually.** It does not evaluate MSBuild first, so a `TargetFramework` inherited
from `Directory.Build.props` is invisible to it — and so is
`$(SomeProperty)` indirection, which was tried and also fails. The symptom is
`No project found` after about a second, with `Analyzing 0 projects` in the debug
log and nothing about why.

The control was in the next directory the whole time: `PokemonBattleJournal` and
`TcgDex.CSharpSdk` both have a `Directory.Build.props`, neither declares
`TargetFramework` in it, and Stryker works in both.

Every project now declares its own `TargetFramework`. That duplicates one
constant across nine files, which is a real cost, and the alternative is that
mutation testing silently does nothing. `Directory.Build.props` carries a comment
saying so, because putting it back looks like a tidy-up.

**Mutation runs must exclude `Tests.Integration`, and `test-projects` alone does
not do it.** Listing only the Unit and Protocol projects there looks like it
scopes the run. It does not: with `solution` set, Stryker discovers every test
project in the solution, and `Tests.Integration` references Protocol
transitively through Host. Observed 2026-08-09 — a run believed to be headless
spent five minutes launching Calculator on repeat.

`solution` is now absent from the config, which is what actually scopes it.
Before any run, read the analysis output and confirm which test projects it
picked rather than trusting the configuration.

**Full mutation runs are long enough to need a diff mode.** A sibling repo of
comparable size takes 60 to 90 minutes for a full run, which is too long to sit
in front of and long enough that it competes for a machine somebody is using.

`--since[:<committish>]` mutates only code changed against a baseline, which is
minutes for a normal edit:

```bash
dotnet stryker --since:main
```

It is a per-change gate, not a score: mutants outside the diff are not tested, so
the percentage it reports is not the repository's. Run it on every change, and a
full run occasionally when the number itself is wanted. `--mutation-level Basic`
generates fewer mutants than the default `Standard`; `--concurrency` trades wall
time for cores and is the worse lever on a long run.

**Two operating rules for these tools, learned the same way.** Mutation testing
gives each mutant a time budget and marks anything slower as `Timeout`, so a busy
machine turns real survivors into false timeouts — the result is wrong, not just
slow — and editing source mid-run invalidates it outright. So: run only when the
machine is free, announce it first, and change nothing while it runs.

**Interrupting the command does not stop it.** The run above continued for about
five minutes after the launching call was cancelled, spawning `vstest.console`
and `testhost` and launching applications the whole time. After stopping any long
run, check for surviving processes and kill them explicitly, then verify the list
is empty — the first sweep missed processes that were still spawning.

**This matters less than it looks, because mutation testing here has been
manual and has been the most productive practice in the project.** Deliberate
mutations, each confirmed to compile cleanly and to fail the intended test, have
so far found: a `/location` test that passed with the coordinate subtraction
removed, a `/text` rule with no test at all, a property test blind to the exact
bug it was written for, and a click assertion that would have passed against a
no-op. Stryker would automate the search; it would not have supplied the
judgement about which survivors matter.

One hazard learned the hard way: a mutation must **compile cleanly**. The first
attempt at removing the runtime-id separator used `&& false` and was rejected by
SonarAnalyzer S1125 — a build failure that looks exactly like an uncaught
mutation if the build output is not read.

**When the fuzzer is built, its corpus should be captured client traffic, not
synthetic input.** Field evidence from a sibling project: a 300+MB corpus of real
files has found more bugs there than any other analysis, static or otherwise.
Real input hits shapes nobody thinks to write, which is a different activity from
testing a hypothesis.

The equivalent here is the other direction of a trick this repository already
uses. `winappdriver-responses.json` records what the real server *sends*; the
fuzz corpus should record what real clients *send us* — request bodies,
capability dictionaries, element ids, locator values, in the shapes the Appium
and Selenium clients actually emit. Those are the untrusted-input boundaries, and
one of them reaches `Process.Start`.

**Corpus size and mutation testing want opposite things, and should not share a
run.** A corpus earns its size by finding unknown-unknowns. Mutation testing only
needs inputs that reach the code, and a mutant surviving one representative input
will survive a thousand. Running a large corpus once per mutant pays exploration
cost to buy discrimination signal — the sibling project's mutation runs take 60
to 90 minutes for exactly this reason, with `--since` already in use. The split
is a coverage-minimized subset for mutation runs and the full corpus on its own
schedule.

**Fuzzing and benchmarking are scaffolding.** `bench/WindowsDriverCore.Fuzz`
and `bench/WindowsDriverCore.Benchmarks` reference SharpFuzz, BenchmarkDotNet,
FlaUI and the Appium client, and both `Program.cs` files throw
`NotImplementedException`. Nothing has been fuzzed and no benchmark has been
run. The FlaUI floor comparison, which is the number that would say how much
headroom is left, does not exist yet.

**Dependency updates are on demand, by decision, until there is a release.**
No Dependabot or Renovate before the first alpha or beta: a bot opens pull
requests on every repository it is enabled for, and on unreleased work that is
noise which trains you to ignore the channel real security news arrives on.

The security half is automatic already and needs no bot — `NuGetAudit=true`,
`NuGetAuditMode=all`, `NuGetAuditLevel=low`, so a known advisory on any package,
transitive included, fails the build. Measured 2026-08-09: zero vulnerable
packages across all eight projects; five outdated, all minor, each a one-line
edit in `Directory.Packages.props` thanks to central package management.

```bash
dotnet list WindowsDriverCore.slnx package --vulnerable --include-transitive
dotnet list WindowsDriverCore.slnx package --outdated
```

**What does run:** the .NET analyzers, Roslynator and SonarAnalyzer, with
`TreatWarningsAsErrors`, `AnalysisModeSecurity=All` and `NuGetAudit` at `low`.
These are not decoration — they failed the build roughly eight times in one
session, including on test code and on a deliberate mutation.

---

## A cache bug the suite found, and the check that missed it

**`CachingElementResolver` served elements from closed windows.** Found
2026-08-10. The diagnostic, printed rather than guessed:

```
window alive: False   cached entries: 1   via cache: Resolved   via fresh walk: NoSuchWindow
```

A fresh walk answered correctly. The cache handed back a live-looking element for
a window that no longer existed — the exact failure the whole handle-caching
design was justified against.

**The cause was the validation property.** Each cache hit checked identity with
`GetRuntimeId()`, which UIA answers **from the proxy** without contacting the
application, so a handle whose window had been destroyed reported its id happily.
`HeldElementLivenessTests` had already measured that a `Current*` read throws
`UIA_E_ELEMENTNOTAVAILABLE` on a dead element — the liveness evidence existed, and
the check used the one property that does not exercise it.

Fixed by probing `CurrentProcessId` before comparing the runtime id: liveness has
to cross to the provider, identity can stay local.

**The test that caught it, and the one that did not.** Seven unit tests cover
this cache with a substituted resolver, including one for "a handle whose
identity changed". All passed throughout — a substituted element cannot die, so
no unit test could reach this. It took a real application being closed.

**And the assertion is now the right one.** It was
`viaCache.ShouldBe(NoSuchWindow)`. It is now
`viaCache.ShouldBe(viaWalk)` first: a cache is only ever allowed to be faster,
never to give a different answer, and comparing the two paths states that
directly instead of restating one path's expected value.

**Wrong instrument, fifth instance.** `GetRuntimeId` stood in for "is this
element alive", and the two are decoupled exactly when the element dies — the
same shape as bytes standing in for content, and presence standing in for
never-evicted. See `PROJECT-KNOWLEDGE.md` section 0.

---

## Our own suite drifts to Windows 11, the same way WinAppDriver's drifts to Windows 10

**Measured 2026-08-10 in a Windows 10 22H2 guest** (build 19045, Calculator
10.1906.55.0 present):

| Environment | Passed | Failed | Skipped |
|---|---|---|---|
| Windows 11 desktop | 113 | 0 | 3 |
| Windows Server 2025 CI | 23 | 0 | 93 |
| **Windows 10 guest** | **98** | **18** | **1** |

The skip count answers the CI question outright: **1, not 93.** Every fixture
found its subject, so the CI gap is absent Store apps on a Server image and
nothing about the driver.

The 18 failures are the uncomfortable part, and they have three roots, all of
them app shape rather than driver defect:

- **Nine failures from one OneTimeSetUp.** `ClickLadderTests` against Settings:
  `Nothing matched ControlType 'Button' within 30s`. Windows 10 Settings does not
  present the tree Windows 11 Settings does.
- **Calculator**: `Nothing matched AutomationId 'num5Button' … (last failure:
  NoSuchWindow)`.
- **charmap**: the toggle the Toggle rung was built around comes back null.

Plus `ActivatingAPackagedApplicationTwice`, on Windows 10's packaged-activation
behaviour.

**This is exactly the criticism this repository makes of the 112/290 score,
pointed back at us.** That number is WinAppDriver measured on Windows 11 against
a suite written for Windows 10 applications, and the argument here has been that
much of it is drift rather than capability. The same is true of this suite in the
other direction: it encodes Windows 11 shapes, and 18 tests say so the moment the
applications change underneath it.

**What follows from it:**

- A test that names a real application's control is a test of that application's
  current layout as much as of this driver. The WPF subject in `apps/` does not
  have this problem — it passed here — which is the strongest argument yet for
  moving coverage onto it.
- Neither score is a capability measurement on its own. Quote the environment
  every time: "WinAppDriver on Windows 11", "this driver on Windows 10".

---

## CI runs 23 of 116 integration tests, because the runner is Windows Server

**Measured on the first CI run, 2026-08-10.** It passed, green tick, zero
annotations — with 93 of 116 integration tests skipped. 87 of them for one
reason:

```
OneTimeSetUp: Calculator is not available: The system cannot find the file specified
```

`windows-latest` is **Windows Server 2025** (`windows-2025-vs2026`). Server ships
without the Microsoft Store and without the inbox UWP apps, so Calculator is not
missing — it is unavailable by construction. No install step fixes it, and no
newer image will. The packaged-application launch path is unreachable there for
the same reason.

**What did run tells the story better than what did not.** Settings and charmap
fixtures executed (their skips are page-specific, not launch failures), and the
purpose-built WPF subject passed every test it owns. It is the only application
that exists on both a developer desktop and a Server runner.

**Two consequences:**

- The fix is to move coverage onto the WPF subject, not to fight the image. A
  ratchet in `ci.yml` fails the build if the skip count rises above today's 93,
  so the debt can only shrink.
- Store-app and packaged-launch coverage needs a **client** Windows machine. A
  self-hosted runner on the Hyper-V VM would cover those *and* supply the matched
  WinAppDriver baseline that every comparison number in this repository currently
  lacks.

**A skip reads as a pass.** This repository has now hit that three times: two
fixtures that silently found no subject in Settings, and an entire CI job.

---

## SetFocus is refused even with the window foregrounded

**Superseded diagnosis, 2026-08-10.** The section below blamed the window not
being in the foreground. Foregrounding is now implemented — `AttachThreadInput`
to the foreground queue, then `SetForegroundWindow` — and it **works**:

```
DIAG foregrounded=True  target=0xA707C4  actual=0xA707C4
     focusable=True     typeWorks=True
```

The window is in front, the element reports focusable, and `SendInput` is
confirmed working. `SetFocus` is still refused by WPF's provider, which leaves
it as the only branch that can fail. So foregrounding was necessary and not
sufficient, and the cause is narrower than "background window".

**Blocks two things:** the Focus rung (`AnEdit_IsClickedByFocusingIt`) and
element send-keys (`SendKeys_TypesIntoTheElement_...`), both `[Ignore]`d against
this.

**A click fallback was tried and reverted.** Clicking to focus drags the entire
ladder in, has side effects on non-text elements, and made an unrelated test take
30 seconds. Worth revisiting as a *targeted* click on the element's own rect
rather than a full ladder pass.

---

## Focus cannot fire against a background window

**The driver never brings a window to the foreground, and UIA's `SetFocus()`
refuses when it is not there.** Measured 2026-08-09 against the WPF test subject:

```
focusable=True  enabled=True  offscreen=False   and SetFocus still refused
foreground=131396   window=3343674
```

Being focusable is not the same as being focusable *right now*. The failure
arrives as `ArgumentException` (E_INVALIDARG), so the Focus rung falls through
and the whole click reports `ElementNotInteractable`.

**Settings and Calculator hid this** by grabbing foreground when they launch. A
small window launched from a test host does not, which is why a purpose-built
subject found it immediately.

**A test fixture cannot work around it**: Windows refuses `SetForegroundWindow`
from a process that is not already foreground. That is precisely why the driver
has to do it, and Microsoft's own `winapp ui click` documents doing exactly that
— "brings the target to the foreground and fails fast … `foreground_not_target`
if focus couldn't be transferred".

`LadderAgainstOwnSubjectTests.AnEdit_IsClickedByFocusingIt` is `[Ignore]`d
against this — asserting the correct behaviour, not the current one, so it is
the specification for the fix rather than a record of the defect.

---

## Enum-valued attributes come back as raw numbers

`ExpandCollapse.ExpandCollapseState` answers `"1"`, not `"Expanded"`. Same for
`Toggle.ToggleState`. **Not yet checked against real WinAppDriver** — the
recordings contain no sample — so it is unknown whether this matches the wire
contract or diverges from it. Do not "fix" it before recording the real server.

---

## The click ladder: six rungs exercised, one still without a subject

A mutation run reported 41 `NoCoverage` mutants in `UiaElementInteractor` —
Calculator is buttons carrying `InvokePattern` and nothing else, so every click
test exercised one rung. `ClickLadderTests` drives Settings, which has the shapes
Calculator lacks.

| Rung | Subject | State |
|---|---|---|
| `Invoke` | Calculator buttons | covered |
| `SelectionItem` | Settings navigation items | covered |
| `ExpandCollapse` | a Settings combo box | covered |
| `Focus` for Edit | the Settings search box | covered |
| **ancestor walk** | a pattern-less element reaching `ancestor:1/Invoke` | covered |
| refusal (`ElementNotInteractable`) | Settings' pattern-less groups | covered |
| **`Toggle`** | charmap, and the WPF subject | covered |
| **guarded mouse** | the WPF subject's pattern-less orphan | covered 2026-08-10 |

**Toggle has no subject in Settings.** Surveyed rather than guessed:
`Button 6 (Invoke=6)`, `ComboBox 1 (ExpandCollapse=1)`,
`ListItem 22 (Invoke=9, SelectionItem=19)`, `Edit 1 (Value=1)`,
`Text 22 (none)`, `Group 20 (none)`, `Hyperlink 3 (Invoke=3)` — not one toggle,
on the landing page or the first six pages navigated to.

That matters because **a checkbox exposes Toggle and not Invoke**, so a ladder
that stopped at Invoke would leave every checkbox unclickable, and nothing here
would notice. `charmap.exe` has a real Win32 "Advanced view" checkbox and is the
obvious next subject.

**Tests choose elements by which pattern they advertise, not by name**, so a
Settings redesign cannot quietly turn one of them into a test of something else.
They assert the *path* rather than success, because "the click worked" cannot
distinguish Toggle from a fallback to Invoke, and that distinction is the point.

**Three of these skipped silently before they were fixed**, each because the
subject was looked for in the wrong place — inside the first selectable item
rather than any, among Buttons rather than Groups. A skip reads as a pass.
`SurveyWhatThisApplicationExposes` is kept `[Explicit]` for exactly this: when a
rung has no subject, measure the application instead of guessing at it.

---

## Deliberate divergences

**W3C `capabilities.alwaysMatch` is rejected.** WinAppDriver understands only
`desiredCapabilities`, measured. Accepting both would create sessions the real
server refuses, so code written against this driver would fail against the one it
replaces — a divergence in the direction that hides bugs.

**Strategy names are matched exactly.** `"Accessibility Id"` is refused. Same
reasoning as above.

**Unrecognised capabilities are dropped from the echo.** Measured: `deviceName`
vanishes, `platformName` survives.

---

## What the tests do not prove

**The founding hypothesis was aimed at the wrong target.** Both issues were read
on 2026-08-08 — see `docs/FOUNDING-PREMISE.md`. #857 is not fixable by any UIA
client, because Inspect.exe cannot see the elements either. #1079 is a
deterministic `FindElement`/`FindElements` disagreement over the same XPath, not
random emptiness and not a caching problem. The experiment below therefore tested
a condition unrelated to either issue, which is why it measured nothing.

The real #1079 experiment is available the moment XPath is implemented: run one
descendant-axis expression through both endpoints on both drivers, and see
whether they agree. That has an obvious control and a sharp prediction.

**The old framing, kept because the measurement is still valid on its own terms:**

The control exists — `FindStabilityComparisonTests` runs the same manipulation
through this driver and through real WinAppDriver. Measured 2026-08-08, 300
iterations each:

| Subject | Empty results | Failed requests |
|---|---|---|
| This driver | 0 / 300 | 0 |
| WinAppDriver | 0 / 300 | 0 |

**Both zero, so the experiment does not support H1.** The manipulation produced
no difference between the subjects, which means the *condition* is insensitive:
an input for which a correct implementation and a broken one predict the same
observation. It says nothing either way about whether #1079 is fixed.

The condition is what needs fixing, not the assertion. Clicking a digit rewrites
Calculator's display but never destroys and re-creates the searched element's
siblings. The field report behind #1079 reproduced it during a `CollectionView`
rebind, with rows removed and re-materialised. Calculator's memory list does
exactly that and is the next condition to try.

Until a condition exists where WinAppDriver demonstrably fails and this driver
does not, **the project's central claim rests on a design argument rather than on
evidence.** Worth stating plainly.

**An unplanned observation from that run, recorded but not yet trusted.** The
same 300 iterations took roughly 33 ms per search through this driver and roughly
1070 ms through WinAppDriver — about 32x. That bears on H3, not H1, and the two
subjects were not measured under matched conditions: this driver ran in-process
while WinAppDriver ran over HTTP and re-clicked through its own element lookup
each iteration. It is a signal worth chasing in the benchmark project, not a
benchmark result.

**One integration failure on 2026-08-09 was never identified.** A full-solution
run reported 1 failed of 82; the run had no trx logger, so the test name was
lost, and it did not recur in seven subsequent runs.

The most likely cause, fixed on the strength of the reasoning rather than a
reproduction: `UiSettle` bounded its wait at 500 *observations* rather than by
time. That is a bound on work for something bounded by time — a cold application
laying itself out — and 500 tight UIA reads is roughly a quarter of a second.
It fits every observation: a `OneTimeSetUp` failure counted as one test, on the
first run after a build, never on a warm re-run. It is now a 30-second deadline,
and the failure message reports elapsed time and observation count so a
recurrence is diagnosable rather than anonymous.

**IDENTIFIED, and it was a product bug rather than a flake.**
`BoundingRectangle_IsTheLabelledFormat_AndAgreesWithScreenBounds` compared the
same rectangle through two routes and they disagreed by a pixel:
`Left:257 Top:615 Width:96 Height:34` from `/attribute` against
`Left:257 Top:616 Width:97 Height:35` from `/size`.

`/attribute/BoundingRectangle` read the raw property — a `double[4]` of
unrounded values — and truncated, while `ScreenBounds` used UIA's own integer
rectangle. The two agree only when the underlying values happen to be integral,
which depends on where the window sits, so it appeared and disappeared. The
whole-solution run made it more likely because the other assemblies' load shifts
timing and layout.

Fixed by rendering that one attribute from the same source `/location` and
`/size` use. Rounding the raw doubles instead would not have worked:
`round(right) - round(left)` and `round(width)` are not the same number, so
agreement needs one source rather than matching arithmetic.

**That was half of it. The rest was the test.** With both routes on the same
function the failure recurred with the same shape — `Top 615` against `616`,
`Width 96` against `97` — which is impossible unless the rectangle changed
between the two calls. It did: the window was still settling. A freshly placed
window keeps adjusting by a pixel or two for a while, and other tests in the run
move windows about, so settling once in `OneTimeSetUp` does not cover a test
that measures later.

The repository owner saw the same thing from the outside: *"when you try to move
the calculator and in some of the tests all I see is a blue block"* — a window
that has been moved before it has painted. Both readings were faithful; they
were taken at two different moments.

Both geometry tests now settle immediately before measuring. Five consecutive
clean whole-solution runs after the change, against roughly one failure in two
before it.

**Two earlier candidates, both wrong, both worth keeping.** A count-based rather
than time-based wait in `UiSettle`, and:

**A second candidate, from the repository owner: physical interference.** The
tests that could have failed that way all assert on window geometry, and a person
nudging or dragging the Calculator window mid-run produces exactly the observed
symptom — bounds that never settle, or a before/after comparison that disagrees.
Neither candidate is confirmed and neither excludes the other.

**Not claimed as fixed.** Run the suite with `--logger trx` so the next
occurrence names itself.

**Interference is a permanent property of these tests, not a bug to close.** Any
test driving a real desktop shares the machine with whoever is using it. The
answer is not retries — a retry turns a real failure into a probabilistic pass —
but diagnosability: a geometry test that fails should say what moved, so a
human-caused failure is recognisable rather than filed as a driver defect.

**Splash-screen handling is partial.** The window search prefers a titled window,
which avoids the common case WinAppDriver is documented as failing. A splash
screen *with* a title still wins.

**The new-top-level-window fallback is loose.** It is the last stage and could in
principle adopt an unrelated window that happened to open in the same moment on a
busy desktop. No test covers that race.

**Concurrency detection is probabilistic.** Substituting a plain `Dictionary` for
`ConcurrentDictionary` did not fail `Add_IsSafeUnderConcurrentUse` on the run
that verified it — only the interleaved add-and-remove test caught it. A race
that does not manifest is not evidence of safety.

**`ApplicationLauncher` argument passing is untested.** `appArguments` reaches
`ProcessStartInfo.Arguments`, and no test opens a file with Notepad to confirm it
arrives.

---

## Behaviours to decide before they become bugs

From field evidence in `docs/PROJECT-KNOWLEDGE.md`, each currently undecided:

- **Scroll-into-view on click** — WinAppDriver does it conditionally, which makes
  a coordinate stale sometimes and not others. Pick one, apply it uniformly, say
  which.
- **`GetText` semantics** — value only, or name concatenated. Silence here
  produces platform-specific test code by accident.
- **Popups and separate top-level windows** — a popup that is its own window is
  invisible to a search rooted at the app window. Needs a stated policy.
- **Pattern-first click divergences** — occlusion, focus, and hover all behave
  differently from a real mouse click. Documented in advance rather than
  discovered through a red compatibility run.

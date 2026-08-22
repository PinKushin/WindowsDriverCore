
> **Every claim below is labelled MEASURED, HYPOTHESIS or REFUTED.** This is a
> clean-room reimplementation, so hypotheses are the working material and being
> wrong is normal — five refuted theories produced the three-stage frame model.
> What is not acceptable is a guess written in the register of a result, because
> nothing downstream can then tell them apart. Refuted theories are kept rather
> than deleted, so nobody re-walks them. The measurements themselves are never
> adjusted to fit: a number that disagrees with the theory kills the theory.
>
> **Hedge the conclusion, never the data.** A theory is written with the doubt
> visible in the sentence — *as far as we can tell*, *that we know of* — because
> the label alone stops being read after the third paragraph. A measurement gets
> no hedging at all, and instead carries the conditions that produced it: which
> commit, which machine, cold or warm, how many runs. Both halves matter. An
> unhedged theory gets built on; a number with no conditions attached is just as
> deceiving, because the reader supplies conditions of their own and they are
> usually the flattering ones.
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

> ## RETRACTED 2026-08-11 — the CoreWindow is NOT destroyed, on either OS
>
> Everything below that rests on "Windows 10 destroys the CoreWindow while
> Windows 11 reparents it" is **refuted**. Same commit, same test, sampled every
> 25 ms from launch:
>
> ```
> Windows 11 host             Windows 10 22H2 guest
> t=  0 ms  handed CoreWindow  t=  0 ms  handed CoreWindow
> t=159 ms  REPARENTED         t=154 ms  REPARENTED
> t=190 ms  frame appeared     t=154 ms  frame appeared
> t=6030 ms IsWindow=True      t=6025 ms IsWindow=True
> ```
>
> **Both reparent. Neither destroys. The timings are within 5 ms of each other,
> so there is no cold-start OS difference in window lifetime at all.**
>
> The original reading came from `EnumWindows` returning nothing owned by the
> application, and `EnumWindows` enumerates *top-level* windows only — it cannot
> tell "destroyed" from "reparented". Those are opposite problems: destruction
> means a session's handle must be replaced; reparenting means the handle is fine
> and only the way it was *found* has stopped working.
>
> **Scope of the retraction.** What is measured is the launch-and-settle path,
> for six seconds, on both systems. Suspend/resume and close-and-relaunch have
> **not** been measured and may behave differently — but no measurement has ever
> demonstrated destruction, so nothing should be built on it.
>
> `PackagedWindowLifetimeTests` is the measurement and runs on both.

## Why attaching to the frame keeps failing — the loose fallback grabs an EMPTY frame

> **CONFIRMED 2026-08-11 — the guess below is now MEASURED, on the wire.** A real
> WinAppDriver 1.2.1 session on Calculator in the Win10 guest, `GET /window_handle`,
> handle resolved through Win32:
>
> ```
> window_handle  = 0x001E024E
> class          = ApplicationFrameWindow
> title          = 'Calculator'
> owning pid     = 3704            (ApplicationFrameHost)
> CoreWindow kid = Windows.UI.Core.CoreWindow(pid=1236)   (the app)
> GA_ROOT        = itself
> ```
>
> **WinAppDriver roots a packaged session at the frame.** We root at the
> CoreWindow, which is destroyed roughly 300 ms after launch — so a session holds
> a handle that dies on its own *and* searches from the wrong element.
>
> This also reclassifies the eight integration tests that failed when the frame
> was returned instead (resolver, cache, packaged lifecycle, reproduced twice on a
> clean machine). They assert the CoreWindow tree, so they are specifying the
> defect and change with the code rather than blocking it. Rooting at the frame
> was reverted on that evidence; the revert was correct at the time and is not the
> destination.
>
> Measured rather than decompiled. WinAppDriver is installed on the guest and
> answers questions about its own contract directly, which is neither a licensing
> problem nor an inference.

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

## The re-resolve is removed: measured inert

The session window re-resolve was worth **+4 net** when it was added. Measured
2026-08-11 at `04dcfaf`, enabling and disabling it gave the **same cold score of
127** and the same passing set except one test each way:

```
only with re-resolve ON  : SwitchWindowsError_NoSuchWindow
only with re-resolve OFF : GetWindowSize
```

The dead-window fail-fast landed in between and subsumed it. So it was carrying
real risk — it can mask a genuinely closed window, which is the exact condition
sixteen `*Error_NoSuchWindow` tests check — for no measured benefit. Removed.

**Two hypotheses died here, both mine.** That the re-resolve was masking those
sixteen: it is not, they fail identically either way. And that the re-resolve was
what fixed the sixteen `ActionsError_*`: they pass with it disabled.

### Those two flip-flopping tests are a SIGNAL, not noise

`SwitchWindowsError_NoSuchWindow` and `GetWindowSize` have now moved in both
directions across several runs. It is tempting to write that off as a noise band,
but that phrasing asserts something not in evidence: that it is meaningless.
**It is unexplained variance, and it may well become consistent once something
else is fixed.** Treat it as a small open question, not as measurement error —
and do not let a ±1 movement decide anything on its own.

## Only a COLD run is a score. A warm run is an instrument.

**CI never gets a warm boot and should not need one**, so a warm number measures
the previous run's residue rather than the driver. Every figure quoted as a score
from here is a cold run: alarm store reset, application not pre-launched.

**That does not mean stop running warm.** The warm run is a deliberate
manipulation, and the *delta* between cold and warm is a real measurement — it
isolates precisely those failures that depend on startup state. Two of the most
useful findings came from exactly that comparison:

| finding | how the delta exposed it |
|---|---|
| the nine add-alarm tests | passed warm, failed cold — pointed at the alarm store |
| the sixteen `ActionsError_*` | passed warm, failed cold — pointed at the session's window dying |

So: run warm to **diagnose**, run cold to **report**. A warm figure in a
comparison table is a bug in the table.

Current cold score: **127/290** at `04dcfaf`. The 133 previously quoted was warm
and should not be used as a ceiling.

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

The climb is the defect. **The first remedy was not, and it cost twelve tests.**

`e9cb249` refused the click outright with `NotInteractable`. **MEASURED
2026-08-11:** `GetElementEnabledState`, `GetElementSizeError_StaleElement` and
`GetElementTagNameError_StaleElement` passed in every cold run from `53ced71`
through `21a18dd` and failed in every run from `05768de` onward; `e9cb249` is in
`05768de` and is not in `21a18dd`. In the `eb0a467` run twelve tests carry our
own `not pointer- or keyboard interactable` message where the suite expected
success or `StaleElementReference`.

**MEASURED, from the suite source.** `ElementEnabled.cs:48` clicks
`ClearMemoryButton` while it is deliberately disabled — its own comment says
"button could initially be already disabled" — and expects the click to succeed.
`AlarmClockBase.GetStaleElement` reaches its subject by clicking `AddAlarmButton`,
which is the button that goes disabled at the cap. So WinAppDriver does not refuse
a disabled element, and eleven `StaleElement` tests plus one `Enabled` test depend
on it not refusing.

Fixed differently: a disabled element gets the **mouse rung and nothing else** —
a click at its own coordinates, no pattern ladder, no climb. That is what a user
gets, and reporting it as performed is honest about what happened.

**Two instrument defects surfaced here, and they generalise past this fix.**

*The subject could not discriminate.* `disabledInsideToggle` sat inside a real
`CheckBox`. A disabled element does not consume mouse input, so the coordinate
click routed to the check box and toggled it — the same observation a wrong climb
produces. Correct and broken predicted the same thing and the test passed either
way. Replaced with `InertToggleHost`: a container carrying `IToggleProvider` and
no mouse handling at all, so a click does nothing and only `Toggle()` moves the
state.

*The fixture was missing a collaborator, which made a refusal unfalsifiable.*
`LadderAgainstOwnSubjectTests` built its interactor with no `IPointerInput`, so
the mouse rung could not run. "The ladder refused" and "the rung was unreachable"
are the same observation when `_pointer` is null, so `APatternlessOrphan_IsRefused`
was green for a reason unrelated to its name. Handing the fixture a real pointer
is what exposed it.

The regression test asserts the **bystander** — the host's `ToggleState`, read
from the other side of the COM boundary — *before* it asserts
`ElementAction.Path`, because the path is the driver's own account of what it did
and a driver that climbed and mislabelled the rung passes that check. Verified by
mutation: restoring the climb moves the host from `0` to `1`, and the bystander
assertion kills it on its own.

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

## /source: the tag names already match. The ATTRIBUTES do not — MEASURED

WinAppDriver 1.2.1, Win10 guest, `GET /session/{id}/source` against Alarms &
Clock. Raw response head, which is the ground truth here:

```xml
<Window AcceleratorKey="" AccessKey="" AutomationId="" ClassName="ApplicationFrameWindow"
        FrameworkId="Win32" HasKeyboardFocus="False" HelpText="" IsContentElement="True"
        IsControlElement="True" IsEnabled="True" IsKeyboardFocusable="True"
        IsOffscreen="False" IsPassword="False" IsRequiredForForm="False" ItemStatus=""
        ItemType="" LocalizedControlType="window" N…
```

Distinct element tags across the document:

```
AppBar, Button, Custom, DataItem, Group, HyperLink, Image, ListItem,
MenuBar, MenuItem, Pane, ScrollBar, Text, Window
```

**Tags are bare control-type names, which is what this driver already emits.**
Nothing to change there.

**The attributes are the gap: roughly 33 against our 5.** Ours carries `Name`,
`AutomationId`, `ClassName`, `IsEnabled`, `ControlType`. A client expression
written against a real driver's source — `//*[@RuntimeId=…]`, anything positional
on `x`/`y`, `[@IsOffscreen='False']`, `[@FrameworkId='XAML']` — matches nothing
here. The root also reports `ClassName="ApplicationFrameWindow"`, which is the
same frame-rooting fact recorded above, arriving independently.

The projection is shared between `/source` and XPath by construction, so widening
it changes both surfaces at once. That is the point of sharing it, and it is also
why the cost has to be measured before it lands: every attribute is a property in
the cache request, and the cache request was measured to win at five properties
and lose at one.

### RETRACTED: "the root element is tagged with the window's NAME"

Asserted here and in memory on 2026-08-11, and **false**. It came from reading
`$root.Name` on an `XmlElement` in PowerShell, whose XML adapter exposes
*attributes* as properties — so `.Name` returned the `Name` **attribute**, never
the tag. `LocalName` and the raw string both say `Window`.

The refutation was already inside the same probe and was walked past: `//Button`
matched **36** nodes in Calculator, which is impossible if tags were names. Two
readings from one probe disagreed, and the one that agreed with the surprise got
published.

Kept rather than deleted, because the failure mode is the durable part — a
measurement taken through a convenience API that silently answers a different
question. Read the raw payload when the shape of a payload is the finding.

## The mouse rung refuses a zero rectangle, and never tries ClickablePoint

Raised 2026-08-11 by the PokemonBattleJournal agent, whose `ClickElement` carries
the comment *"Do NOT reach for the bounding rectangle here. These buttons report
0x0 to Appium."*

**Confirmed here.** `ClickWithTheMouse` refuses when the bounding rectangle is
degenerate:

```csharp
if (rect.right <= rect.left || rect.bottom <= rect.top)
    return ElementAction.Failed(ElementActionOutcome.NotInteractable);
```

**Two things make this wider than it looks.**

First, it is no longer reachable only by pattern-less elements. Since `c26e4d3` a
**disabled** element goes straight to the mouse rung, skipping the pattern ladder
and the ancestor walk, because refusing disabled clicks cost twelve compatibility
tests. Any disabled control reporting 0x0 now meets this check directly.

Second, `NotInteractable` out of that one rung has **five** distinct causes — no
pointer or window locator injected, the provider throwing on
`CurrentBoundingRectangle`, the zero rectangle, `GetBounds` returning null, and
the computed point falling outside the window. A caller cannot tell which, which
makes a genuine 0x0 indistinguishable from a wiring mistake. That is a
diagnosability defect of ours.

**`ClickablePoint` is never used for clicking.** UIA property 30014 is exposed as
a readable attribute and nothing else; the click point is always the centre of the
bounding rectangle. `GetClickablePoint` often succeeds on exactly the elements
whose rectangle is degenerate, so it is the obvious candidate — **not implemented,
and not measured**.

**ANSWERED, and the ClickablePoint suggestion above is withdrawn.** Reading the
PokemonBattleJournal source rather than proposing an experiment: those buttons
*"report 0x0 to Appium for their whole life **and invoke perfectly through
FlaUI**"*, and its off-window guard reports "outside the app window" — which is a
0,0,0,0 rectangle yielding a centre of (0,0). So the geometry really is degenerate
in UIA and the element is still invokable **by pattern**. `GetClickablePoint`
generally fails on a zero-area element too, so it would not have helped.

**The real overlap is a defect of ours, and it is not about geometry.** The same
source records `ApplyConflictsButton` carrying *no pattern at all* for a beat, and
invoking cleanly 70 ms later — WinAppDriver publishes an element as soon as it is
realised. Our click is **one shot**:

```csharp
MapAction(app, "click", static (interactor, window, id) => interactor.Click(window, id));
```

The implicit wait retries the *find*, never the *action*. So an element that is
momentarily pattern-less falls through the whole ladder to the mouse rung, meets
the zero rectangle, and is refused as `NotInteractable` — for an element that
would have invoked a poll later. That is a race we would lose exactly where PBJ
lost it, and it is invisible to the compatibility suite, whose subjects are long
since realised.

Not yet measured: whether retrying the ladder within the implicit wait fixes it
without making a genuinely unclickable element cost the full wait. That trade is
the same one the dead-window fail-fast had to solve, and it should be tested by
CALL COUNT rather than elapsed time.

## Rooting at the frame: 150 -> 166, and the reason was NOT the one predicted

MEASURED, cold, Windows 10 22H2 guest, alarm store reset, one run each:
`e045b06` **150**/290, `c26e4d3` **166**/290. **Sixteen gained, none lost — a
strict superset.**

```
GetWindowSize                        SetWindowSize
SetWindowPosition                    SetWindowPosition_ToOrigin
MaximizeWindow                       MouseDoubleClick
GetWindowSizeError_NoSuchWindow      SetWindowSizeError_NoSuchWindow
GetWindowPositionError_NoSuchWindow  SetWindowPositionError_NoSuchWindow
MaximizeWindowError_NoSuchWindow     MouseDownMoveUp
GetElementEnabledState               GetElementEnabledStateError_StaleElement
GetElementEnabledStateError_NoSuchWindow
CreateSessionFromExistingWindowHandleError_NonTopLevelWindowHandle
```

**Nine of the sixteen are window management.** `SetWindowPos`, `GetWindowRect`,
maximize. Rooted at the `CoreWindow` those addressed a *child* window, where
position and size are not meaningful quantities; rooted at the frame they address
the real top-level window. The two mouse tests follow for the same reason — a
click coordinate is relative to a window that actually has a position.

**The prediction was reach, and reach is not what paid.** The argument for the
frame was that its subtree carries `ApplicationFrameTitleBarWindow` — 53 elements
against 45 — so title-bar chrome becomes findable. No chrome test moved. What
moved was every operation that treats the session's handle as *the window*.

That is the third time on this seam that a correct change has been justified by
the wrong mechanism, after "the CoreWindow is destroyed" (retracted, it is
reparented) and "the empty frame must be refused" (retracted, it cost 20 tests).
The change stands on the measurement, not on the story attached to it.

## The coordinate click is guarded by the wrong question

`BringToForeground` returns `bool`. Both call sites discard it:

```csharp
_windows?.BringToForeground(window);
```

`SetForegroundWindow` refuses when the calling process does not hold foreground
rights — a documented Windows restriction, and one this repository has already
measured in another form (UIA refuses `SetFocus` against a background window). So
the driver can fail to raise the target and dispatch a coordinate click anyway,
into whatever is actually in front. That is the **unguarded coordinate click**
this project exists to fix, arriving through a different door.

It also violates a standing rule: a return-value error signal must be checked
before reading dependent state.

**The guard that exists is the wrong question.** `ClickWithTheMouse` asks *is this
point inside the target window's rectangle*. What matters is *is the target window
the one at this point*, and `WindowFromPoint` answers exactly that — a window
that is covered fails the second test and passes the first.

**Fixed at `008f87a`** with `IWindowLocator.OwnsThePointAt`, plus one bounded
re-attempt at raising the window before refusing.

**It gained nothing — but the reasoning behind it was RIGHT, and the retraction
written here was wrong.** Both corrections are kept, because the sequence is the
lesson.

The guard was built on a story: the WOW64 fix let the suite open File Explorer
sessions whose windows outlive them, and those windows were costing the mouse
tests. The guard did not recover them, so the story was retracted as unsupported.

**Then the desktop was cleaned and they both came back.** MEASURED: the same
commit `008f87a` scored **167** with 22 File Explorer windows open and **169**
after closing them, and the two tests gained were exactly `MouseDoubleClick` and
`MouseDownMoveUp`. Seven windows accumulate per run and nothing removed them —
they belong to the shell process, so no process kill touches them and the
`LEFT BEHIND` report could not see them.

**A failed fix does not falsify the hypothesis it was built on. It falsifies the
mechanism.** The windows really were the cause; "they cover the click point" was
the wrong account of how, and the guard addressed only that account. Treating the
guard's failure as evidence against the environment was the error, and it led to
declaring a real, reproducible effect to be flake.

With the environment controlled, all five runs explain themselves and none of them
is flaky:

| run | root | desktop | mouse pair |
|---|---|---|---|
| `e045b06` | CoreWindow | clean | Failed |
| `c26e4d3` | frame | clean, Explorer could not launch yet | Passed |
| `0cdadc6` x2 | frame | dirty | Failed |
| `008f87a` | frame | dirty | Failed |
| `008f87a` | frame | clean | Passed |

Two independent causes — the wrong window root, then desktop contamination — read
as one noisy signal for as long as neither was controlled.

Across five runs they read:

```
e045b06 Failed   c26e4d3 Passed   0cdadc6 Failed   0cdadc6 Failed   008f87a Failed
```

**One pass in five.** The "baseline" was a single passing run, and the regression
was inferred from it — the one-observation error, made while the rule against it
was being quoted. The pair mostly fails; `c26e4d3` was the outlier.

The measurement also refutes the covering-window story directly. If something were
in front, the new guard would now *refuse* and the suite would report a driver
error. It still reports assertion failures — `Expected any value
except:<{X=120,Y=0}>` — so the clicks reach our window and simply do not have the
expected effect. Whatever is wrong with the mouse family, it is not where the
click lands.

**The guard is kept on its own merits, not on a score.** A driver that can fail to
raise a window and then dispatch synthesized input into whatever is in front is
the founding defect of this project, and `BringToForeground`'s result was being
discarded. That is worth fixing whether or not any test moves. Claiming otherwise
would be reading the number backwards into the reasoning.

## ROADMAP: an explicit "this lookup is allowed to fail" flag

Requested by the PokemonBattleJournal agent as acceptance criterion `A5`, and
wanted by this repository's owner. **Not scheduled — recorded so it is not
rediscovered.**

**The problem it solves.** A suite that asks "is this element present?" has no way
to say that absence is a legal answer. The only lever is the ambient implicit
wait, so PBJ sets it to zero, does the lookup, and sets it back. That leaks: if an
assertion throws in between, the wait stays at zero for every later test in the
process, and the failures land somewhere else entirely.

**Design constraints, before anything is written:**

- **It is a vendor extension, not a protocol feature.** JSON Wire has no
  expression for it, so per this project's own rule the extension is explicit
  rather than a silent reinterpretation of an existing route — the same reasoning
  that produced `windows: invoke` versus `windows: mouseClick`.
- **It must not be a second ambient mode.** A session-scoped "optional lookups"
  setting reproduces the leak it exists to fix. The flag belongs on the request.
- **The answer must stay distinguishable.** "Found nothing, and that was allowed"
  is not the same as "found nothing, and that is an error"; a client needs to tell
  them apart without parsing a message.
- **It interacts with the implicit wait.** An optional lookup that still burns the
  full wait solves half the problem — PBJ's measured cost was six five-second
  stalls in one fixture. Whether optional implies "do not wait" or "wait, then
  answer cleanly" is the first thing to settle, and it is a question for the
  consumer rather than for the driver.

## The guest DOES root at the frame — a retraction of a retraction

**MEASURED at `cc5b271`, Windows 10 22H2 guest**, with the application genuinely
killed first:

```
COLD launch : 838 ms, class=ApplicationFrameWindow
re-attach   : source=HostedFrame, 4 ms
Windows 11  : cold 733 ms ApplicationFrameWindow, re-attach HostedFrame 11 ms
```

Frame rooting works on both systems, cold and warm. **Three claims made earlier
today are withdrawn:**

- that the guest never gets a frame-rooted session
- that the +16 at `c26e4d3` was therefore won by something other than frame rooting
- that a cold packaged launch pays the full ten-second timeout

None of those happen. The original attribution of the +16 stands.

**Two bad instruments produced them, both mine.**

The first was a probe that started the driver from the guest's `bin\Debug` without
rebuilding. That build was from `9d117ce` — *before* frame rooting existed — so it
faithfully reported a `CoreWindow` for a driver two commits old, and the reading was
attributed to current code. This is the `--no-build` hazard in a different costume:
the flag was not used, but a binary was still run without being rebuilt from the
commit under discussion.

The second was the replacement measurement, which called
`AppLifetime.KillAll("CalculatorApp")` before its "cold" launch. That matches on
substring and Windows 10 names the process `Calculator`, so it killed nothing and
measured a re-attach while claiming to measure a cold start. It reported 616 ms and
a frame, which looked like a clean refutation and was not a cold launch at all.

**What made it recoverable was the contradiction, not the assertions.** Two
measurements of one thing disagreed. Every assertion in both passed. The rule that
worked: when two measurements disagree, suspect the instruments before believing
either — and check what commit the binary under test was actually built from.

## UNREACHABLE: eight suite tests need legacy Edge, which is not installed

**MEASURED on the Windows 10 22H2 guest:**

```
Get-AppxPackage Microsoft.MicrosoftEdge   -> NOT INSTALLED
Edge-ish packages present                 -> Microsoft.MicrosoftEdge.Stable
                                             Microsoft.MicrosoftEdgeDevToolsClient
```

The suite's `CommonTestSettings.EdgeAppId` is
`Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge` — **legacy EdgeHTML**, which
22H2 replaced with Chromium Edge under a different package identity. Activation
therefore fails, `ActivatePackagedApplication` returns `processId == 0`, and the
driver answers "The system cannot find the file specified".

**That message is correct.** It is the same string WinAppDriver returns, and
`Session.cs:132` asserts it for a genuinely absent application. Nothing here is a
driver defect.

**What is blocked:**

| fixture | tests | how |
|---|---|---|
| `TouchClick` | 2 | derives from `EdgeBase`, dies in `ClassInitialize` |
| `TouchFlick` | 1 | derives from `EdgeBase`, dies in `ClassInitialize` |
| `Back` | 1 | `NavigateBack_Browser` |
| `Forward` | 1 | `NavigateForward_Browser` |
| `Session` | 1 | `CreateSessionWithArguments_ModernApp` |
| `Window` | 2 | `GetWindowHandles_ModernApp`, `SwitchWindows` |

**Eight tests, and no amount of driver work reaches them.** The three Touch
failures are the ones worth flagging: they look like touch defects in a failure
list and are nothing of the kind, because a `ClassInitialize` failure names the
fixture rather than the cause.

This matches WinAppDriver's own ceiling — its 281/290 on this guest includes
failures for an absent browser, so the comparison remains fair; both drivers lose
the same tests for the same environmental reason.

**Consequence for the target — the ceiling was 281 and is now 231.**

> **RE-MEASURED 2026-08-11 after the Alarms & Clock update** (see the drift section
> below). WinAppDriver 1.2.1, same guest, same suite, store reset and app warmed:
> **231/290, 59 failed** — `run-4725072-WinAppDriver-140334.trx`. Fifty tests worse
> than the 281 below, and the diff against the old failure set is **50 newly
> failing, 0 recovered**: a strict regression, not a reshuffle.
>
> **So the live ceiling is 231, and our 169 was measured against the same
> application.** The gap is **62 tests**, not the 112 computed against the stale
> baseline. That is the number to work against.
>
> The 50 are dominated by tests whose subject is Alarms & Clock — the eleven
> `*Error_StaleElement`, `ClickElement`, `GetElementText`, `GetElementAttribute`,
> `GetElementDisplayedState`, `GetElementScreenshot`, the `FindNestedElement*`
> family, `FindElement_ByClassName`, `FindElement_ByXPath` — plus twelve `Touch_*`
> and `Pen_*` Actions tests and a few others (`Launch_ModernApp`,
> `NavigateBack_SystemApp`, `MiscellaneousSession_MultiSessionsSingleInstance`).
>
> **Attribution, stated honestly:** what is measured is that *the guest changed*
> between 08-10 13:53 and now. The change that can be named is the app version, and
> the failure set is consistent with it. Other guest changes in that window were not
> ruled out, so "the app update cost 50 tests" is the leading reading rather than an
> isolated manipulation.

The 281 figure below is kept because it is what the pre-update guest measured, and
because the reasoning around it was corrected at the same time.

Read straight out of `run-winappdriver121-matched-134208.trx`, WinAppDriver 1.2.1
on this guest **before the update**, 290 run / 281 passed / 9 failed:

```
NavigateBack_Browser                  browser navigation, no Edge
NavigateForward_Browser               browser navigation, no Edge
TouchSingleTap                        EdgeBase, dies in ClassInitialize
TouchSingleTapError_StaleElement      EdgeBase, dies in ClassInitialize
TouchFlick_Arbitrary                  EdgeBase, dies in ClassInitialize
CreateSessionWithArguments_ModernApp  needs a UWP app that will not install
GetWindowHandles_ModernApp            needs a UWP app that will not install
SwitchWindows                         needs a UWP app that will not install
GetLocation                           geolocation; a VM has no provider
```

That is **exactly** the eight in the table above, plus `GetLocation`. Every one was
environmental, so the ceiling on the *pre-update* guest was **281** and WinAppDriver
reached it precisely — it left no capability on the table. All nine still fail
today; they are the first nine of the current 59.

**Two corrections, both to text written on 2026-08-11.**

*The 279 figure was wrong, but not for the reason given when it was fixed.* It
subtracted "eight Edge tests and three more needing an uninstallable UWP package",
and the fix claimed WinAppDriver refutes the second subtraction by passing those
three. **It does not pass them** — `CreateSessionWithArguments_ModernApp`,
`GetWindowHandles_ModernApp` and `SwitchWindows` are right there in its failure
list. The actual error was double counting: those three are *inside* the eight,
not additional to them. 281 was the right number reached by wrong arithmetic, and
a right answer from wrong reasoning is worth less than it looks.

*"Eight need legacy EdgeHTML" is loose.* Five are browser-related — two navigation
tests and three that derive from `EdgeBase` and die in `ClassInitialize`. The other
three need an absent UWP package and have nothing to do with Edge. A failure list
grouped by fixture hides that, which is how the description drifted from the data
in the first place.

## The scaffolding was the confound (2026-08-11)

**Every score this project recorded between 2026-08-10 09:38 and 2026-08-11 was
taken through two pieces of scaffolding that moved the result.** Both were added
in one commit, `c62ebde`, to stabilise the number:

- an **alarm-store reset** before each run
- an **app-warm step** that pre-started Alarms & Clock

Measured on the rebuilt offline guest — Windows 10 19045, Alarms & Clock
`10.1906.2182.0`, static 4 GB, cold, WinAppDriver 1.2.1, same commit throughout:

| condition | score | `ActionsError` failures |
|---|---|---|
| reset + warm | 281 | 0 — the warm hid the damage |
| reset, no warm | 259 / 259 / 260 | **21** |
| **no reset, no warm** | **280** | **0** |

### Why the reset breaks it

The reset moves the application's entire `Settings` folder away, so the next
launch is a genuine cold start rather than a re-attach. **A cold activation
returns an intermediate process id**, and both drivers then search for a window
owned by a process that does not own it.

Our own request transcript caught it directly:

```
17:16:49.230  pid  3024   0x30218  !! NO CLASS !!        10241.6 ms   <- failed
17:16:50.279  pid  4852   0x808EA  ApplicationFrameWindow   790.0 ms   <- 1s later, fine
   every later session: pid 4852, 25-89 ms
```

Activation returned **3024**; the application was running as **4852** and visible
on screen the whole time. WinAppDriver loses the same race — its error names a
pid too — and fails the session honestly. We returned a handle that was no longer
a window and reported `200 jwp 0`, which turned one bad session into **105
failing finds**. Fixed in `044b71c`; the pid handling itself is still open and is
where tests are actually recovered.

### Why the reset existed at all — the workaround outlived the bug

It was not arbitrary. When it was added, **alarms were not being deleted**: the
suite's own `DeletePreviouslyCreatedAlarmEntry` was failing against this driver,
so the store filled to the application's cap, `AddAlarmButton` went disabled, and
nine tests died together. Resetting the store was a reasonable answer to a real
defect.

The deletion path was later **partially** fixed, and "partially" is the operative
word. Observed 2026-08-11 after two separate runs: most alarms are gone, and
**two are left behind every time**. So the chain — the XPath find, the right-click
through `/moveto` and `/click` with button 2, and the
`FindElementByName("Delete")` on the context menu — works for most entries and
fails for some.

`DeletePreviouslyCreatedAlarmEntry` wraps its four steps in a bare
`catch { break; }`, so the first failure ends the loop silently and everything
after it stays. Two per run is nowhere near the cap on its own, but it
**accumulates**: enough runs and the store reaches the application's limit,
`AddAlarmButton` goes disabled, and the nine-test failure the reset was invented
for returns — this time with no reset to hide it.

That makes the store reset **not fully obsolete after all**. It compensates for a
leak that still exists. The right shape is not per-run resetting, which costs 21
tests, but resetting *occasionally* — or fixing whatever makes two of them
undeletable, which is the better answer and is not yet diagnosed. The transcript
records every find and click, so the failing step is recoverable from a run rather
than needing a new probe.

Nobody removed the workaround. It stopped compensating for anything and carried
on costing 21 tests, and because the warm step added in the same commit hid the
cost, the pair looked harmless for a day.

**The general shape is worth more than this instance:** a workaround added for a
real defect becomes invisible scaffolding the moment the defect is fixed, and it
is never load-bearing in the direction anyone expects. Anything added here to
stabilise a measurement needs the defect it compensates for written next to it, so
that fixing the defect prompts removing the workaround.

### What 231 still means, and why it cannot be re-measured

The 231/290 taken against the drifted Alarms & Clock `11.2606.11.0` is NOT
invalidated by the scaffolding finding, because it was measured under the same
conditions as the 281 it was compared against — its log records both
`alarm store reset` and `app warmed (AddAlarmButton enabled): True`. Under that
pair the reset's damage and the warm's compensation roughly cancel, which is why
the good app scored 281 with them and 280 without.

So **281 against 231 is a fair app-drift comparison**, and the ~50 tests between
them are the drift itself: `AlarmSaveButton`, `AlarmNameTextBox` and
`CancelButton` are gone from that build, and no scaffolding recovers them.
Removing the scaffolding would likely have left it near 230 rather than lifting it
to 252 — the reset's 21 were already being paid back by the warm.

**It CAN be re-measured, and the checkpoint is what makes that safe.** An earlier
version of this paragraph said the number was frozen forever because the drifted
guest was deleted and the rebuilt one is offline. That is wrong: reconnect the
adapter, let the Store update Alarms & Clock, run the suite with no scaffolding,
then restore the `pristine-alarms-10.1906.2182.0` checkpoint. The instrument
survives the experiment, which is the entire reason the checkpoint was taken
before anything touched the guest.

Two caveats on what such a run would prove. The Store would deliver whatever
version is current, which may be newer than `11.2606.11.0` — so it re-measures
"the drifted app without scaffolding", not that specific run. And the network has
to be reconnected, which is the exact hazard the offline build exists to prevent;
it must be disconnected again before the checkpoint is restored, or the next
rebuild inherits the problem.

Until then 231 stands as measured, under reset+warm, comparable to 281 and to
nothing else.

### What this invalidates

`169`, `231`, `163`, `164`, `259` and the "62-test gap" all appear in this
repository's history. **None of them are comparable to each other or to 280.**
They differ in app version, in whether the store was reset, and in whether the app
was warm.

### The lesson worth more than the number

**Three careful runs agreed on 259 and all three were wrong.** Host load varied
62/40/54%, guest memory went from dynamically squeezed to statically pinned, and
the failure count did not move — which read as robustness. It was not.
Reproducibility is not validity when what is being reproduced is the confound.

The owner said the very first run scored 281 and held that position while three
separate explanations were constructed for why it could not have. The memory was
right and the measurements were wrong, because the measurements were taken through
scaffolding that did not exist when that run happened.

Resetting is now opt-in (`-ResetStore`) and costs 21 tests when used.

## The stale-element cluster: measured to the point where OUR bug starts

Eleven `*Error_StaleElement` tests assert
`"An element command failed because the referenced element is no longer attached
to the DOM."` and receive one of our own messages instead. That reads as a
fault-mapping bug. It is not one.

### Where the message actually comes from

`ElementFault.For` handles `Read`, `NotFound` and `NoSuchWindow` and **throws** on
anything else, so it can never emit `NotInteractable`. That message comes only
from `ElementActionRoutes` — an action. But `GetElementSizeError_StaleElement` and
its siblings are **reads**. So the failing command is not the one under test.

`AlarmClockBase.GetStaleElement()` reaches its subject through two calls before
the test's own:

```csharp
session.FindElementByAccessibilityId("AddAlarmButton").Click();
Thread.Sleep(500);
WindowsElement staleElement = session.FindElementByAccessibilityId("AlarmSaveButton");
```

Whatever those throw leaves the helper, and each test's
`catch (InvalidOperationException)` compares **the helper's** message with the
expected stale string. Eleven tests, one upstream failure.

### MEASURED, on the guest, through this driver

```
STEP 0  //ListItem count                = 6
        AddAlarmButton enabled          = True
        AddAlarmButton click            = 200
        AlarmSaveButton find            = 404 no such element
        page source AutomationIds       53 before -> 68 after the click
        NEW: EditFlyout, DurationPicker, HourPicker, MinutePicker,
             RepeatCheckBox, ChimeComboBox, SnoozeComboBox,
             PrimaryButton, CloseButton, Light Dismiss
```

**The click works.** It opens the add-alarm flyout; fifteen elements appear that
were not there before. What is missing from our view of that flyout is
`AlarmSaveButton`.

### The app is NOT drifted, and assuming it was cost a wrong conclusion

`EditFlyout` with `PrimaryButton`/`CloseButton` looks like a newer UI than the
suite was written for, and that was written here as the explanation. **It is
wrong.** WinAppDriver 1.2.1, same guest, same application, passes **21 of 22**
`*StaleElement* tests, and its only failure among them is
`TouchSingleTapError_StaleElement`, which derives from `EdgeBase`.

Its nine failures overall are **eight Edge tests and `GetLocation`** — no alarm
tests at all. WinAppDriver targets a slightly earlier Windows 10 than this guest
and still passes these, so the application has not moved out from under the suite.

`AlarmSaveButton` is therefore reachable on this machine, and **we are the ones
who cannot find it.** The check that settles a drift claim is the reference
driver's outcomes on the same machine, and it was available the whole time.

### The tree-view candidate: mechanism CONFIRMED, cause REFUTED

The proposal was that `IUIAutomationElement::FindAll` filters to the **control
view**, hiding `AlarmSaveButton` from every locator while a raw walk would reach
it. Probed 2026-08-11 (`WhichViewAFindWalksTests`, Windows 11 host, Calculator).

**The mechanism is real, and larger than expected.** From the same root:

```
FindAll(TreeScope_Descendants, true condition)  =  73 elements
raw TreeWalker descent                          = 125 elements
reachable ONLY by the raw walk                  =  52
reachable ONLY by FindAll                       =   0
```

A strict subset, and **all 52 have `IsControlElement=False`**. It is not confined
to anonymous scaffolding either — four carry real automation ids: `AppIcon`,
`TextContainer`, `NormalOutput`, `ParenthesisCount`. The filter comes from the
cache request's `TreeFilter`, whose default is the control view; setting it to a
true condition and calling `FindAllBuildCache` reaches exactly 125, the raw set.

**And it is not a defect, because WinAppDriver has it too.** Measured the same
day, same host, through WinAppDriver 1.2.1:

```
num5Button        -> FOUND        (IsControlElement=True)
NormalOutput      -> not found
TextContainer     -> not found
ParenthesisCount  -> not found
AppIcon           -> not found
GET /source (36242 chars) contains num5Button, and none of the other four
```

So the control view is **parity**, not a limitation to fix. Pulling the
`TreeFilter` lever would emit 125 nodes from `/source` where the reference driver
emits 73 and would double what `//Text` matches — a silent divergence dressed as
a capability win. The test keeps the lever measured and deliberately unused.

**The cause is refuted by the same measurement.** WinAppDriver shares the
limitation, so the view cannot be what separates us from it. This is the third
time in this repository a confirmed mechanism was about to be credited with a
failure it did not cause; the check that costs ten minutes is asking the reference
driver whether it has the same constraint.

### And the button is gone: THE APP UPDATED MID-INVESTIGATION

Probed on the guest, through WinAppDriver itself, store reset and app warmed:

```
click AddAlarmButton at t=480 ms: ok
polling one-shot finds (implicit_wait=0) for 8 s:
    t= 3172 ms  PrimaryButton APPEARED
first seen (ms after the click, -1 = never within 8 s):
  AlarmSaveButton    -1
  AlarmNameTextBox   -1
  CancelButton       -1
  PrimaryButton      3172
GET /source (37413 chars) contains AlarmSaveButton = False
```

`AutomationId="PrimaryButton" ClassName="Button" Name="Save"
IsControlElement="True"`. The element the suite asks for does not exist, it is not
hiding in a view we do not walk, and no implicit wait reaches it.

**Alarms & Clock updated to `11.2606.11.0` on 2026-08-10**, in the gap between the
WinAppDriver baseline TRX (written 13:53) and the runs that resumed at 17:12; the
package folder was created at 16:38. `docs/memory/016` catalogued this exact set of
renames on 2026-08-08 — `AlarmSaveButton` → `PrimaryButton`, `CancelButton` →
`CloseButton`, `AlarmNameTextBox` → no automation id — **as Windows 11 drift**. The
guest's Store app has caught up to the host's, so 016 is no longer a Win11
document.

`AlarmClockBase.GetStaleElement()` has no version fallback, unlike
`FindAlarmTabElement()` which handles two Alarms generations. So eleven
`*Error_StaleElement` tests plus everything routed through `AddAlarmEntry` are now
unreachable **by any driver on this guest**, WinAppDriver included.

### What that costs the ground truth — MEASURED

The 281 baseline was measured against a **different application** than every score
taken after 2026-08-10 16:38 — which is all of them from `05768de` onward,
including 169. So the reference driver was re-measured on the current guest:

| | app | WinAppDriver | ours | gap |
|---|---|---|---|---|
| before the update | earlier Alarms | **281**/290 | — | — |
| after the update | `11.2606.11.0` | **231**/290 | **169** | **62** |

**50 newly failing, 0 recovered.** The old "112 tests, all capability" was computed
across the boundary and is wrong by 50; the real backlog is **62**.

Three consequences:

1. **The reference driver has to be re-measured**, not reused. A baseline is a
   measurement of a machine at a time, not a constant.
2. **The guest was chosen because the suite was readable there** — 281 against 112
   on Windows 11. Whatever part of that advantage came from an older Alarms build
   is gone.
3. **An instrument that updates itself is not an instrument.** Record the app
   version beside any score, and stopping Store auto-update on the guest is now
   worth doing.

### The suite's own blindfold, still worth knowing

`DeletePreviouslyCreatedAlarmEntry` wraps its four steps in a bare
`catch { break; }`, so if deletion ever does fail against this driver, alarms
accumulate silently until the cap disables `AddAlarmButton`. That is a real
mechanism — it cost nine unrelated tests once already — but it is **not** what is
failing here: the button was measured enabled and its click returned 200.

## Diagnostics: the request transcript

**Added 2026-08-11, because its absence was measured in wasted days.** Every
question asked of this driver during the compatibility work — which request
failed, with what status, at what point in a flow — needed a bespoke probe, since
the server said nothing about what it had answered. WinAppDriver prints its own
transcript, and that is the only reason its failures are readable.

Console by default, or a file when `WINDOWSDRIVERCORE_LOG` names one. Requests at
the margin, the work they caused indented under them. A real capture:

```
...01.209Z GET /status -> 200 jwp - 212.2 ms
...01.977Z   launch 'Microsoft.WindowsCalculator_...!App' -> pid 27128 window 0xDC0858 (ApplicationFrameWindow) 733.3 ms
...01.984Z POST /session -> 200 jwp 0 749.7 ms
...02.068Z   find AutomationId='num5Button' -> 1 match(es) 54.9 ms
...02.070Z POST /session/{id}/element -> 200 jwp 0 78.6 ms
...02.995Z     resolve -> Resolved 32.7 ms
...03.006Z   Click -> Performed via Invoke 43.7 ms
...02.126Z POST /session/{id}/element/42.462484.4.102/click -> 200 jwp 0 51.8 ms
...02.173Z   find AutomationId='NormalOutput' -> 0 match(es) 40.6 ms
...02.182Z POST /session/{id}/element -> 404 jwp 7 49.8 ms
...21.217Z   source -> 9502 chars 46.9 ms
...21.221Z GET /session/{id}/source -> 200 jwp 0 52.1 ms
...21.273Z   terminate pid 29364 -> ended 44.4 ms
...02.259Z DELETE /session/{id} -> 200 jwp 0 58.3 ms
```

Read what that answers, because each was a bespoke probe before:

- **Where the time went.** The find cost 54.9 ms of the request's 78.6 ms, so
  23.7 ms is this driver's own overhead — which is exactly the quantity
  `bench/WindowsDriverCore.Benchmarks` exists to shrink.
- **Which rung clicked.** `via Invoke`. A pattern, an ancestor climb and a real
  mouse click all report status 0, and when the climb toggled an app bar instead
  of pressing "Add new alarm" nothing downstream could tell.
- **Whether a find failed or simply matched nothing.** `NormalOutput` ran and
  returned 0, so the element is absent from the control view — a fact about the
  application. A search that could not run reads `FAILED: NoSuchWindow` instead.
- **How the application was reached.** 733.3 ms, and an `ApplicationFrameWindow`
  rather than a `Windows.UI.Core.CoreWindow`. Both halves matter: the window
  search times out at ten seconds, so a launch near that number ran out rather
  than succeeded, and the CLASS separates "the frame answered" from "a CoreWindow
  was held to the deadline and returned". Three separate claims about that search
  were each credited to the wrong mechanism because the handle was the only
  observable.
- **Whether the application actually died.** `terminate pid 27128 -> ended`. A
  false there reads `STILL RUNNING`, and it is how the next run inherits a warm
  application it did not ask for and measures a re-attach as a cold launch —
  misread as a code change twice already.

Four decisions worth keeping:

- **The JSON Wire status is its own column.** It is not derivable from the HTTP
  code: 404 covers status `7` and status `9`, and 400 covers `10`, `23`, `100`
  and `105`. The two 404s above are completely different faults.
- **`jwp -` means no envelope**, not status zero. `GET /status` genuinely has
  none, and printing `0` would read as a success that never happened.
- **The line between a query and a payload, stated rather than implied.** A
  *locator* is recorded, value and all — it is what the test author wrote, and no
  find failure is diagnosable without it. What is never recorded is anything this
  driver **transmits into** the application: `SetValue` and `SendKeys` arguments,
  and a launch's command-line. `IInteractionLog` has no parameter that could take
  them, so this is a property of the shape rather than of a redactor that has to
  stay correct — and two tests push a password through both routes and assert it
  appears in nothing the decorator hands the log.
- **Nothing leaves the machine, structurally.** `EventSource` publishes
  in-process and a consumer attaches; the only destinations that exist are a
  console and a file. There is no sink, endpoint, or transport anywhere under
  `WindowsDriverCore.Diagnostics` to misconfigure.

`EventSource` rather than a logging package: in-box, so no dependency and no
supply-chain surface, and a `WriteEvent` behind an `IsEnabled` guard costs a
volatile read when nothing is listening. The five Serilog references that had sat
unused in `WindowsDriverCore.Host.csproj` since the start were removed in the same
change.

**The automation layers are logged by decorators, not by themselves.**
`UiaElementFinder` and `UiaElementInteractor` are constructed at nineteen call
sites across fifteen files, nearly all UI tests that drive a real desktop.
`LoggingElementFinder`, `LoggingElementInteractor` and `LoggingApplicationLauncher`
wrap them; the real classes are untouched, the wiring is one registration each in
the composition root, and the decorators are unit-tested against fakes in
milliseconds. Each one records the exception type and **rethrows** — an action
that threw is the line most worth having, and swallowing it would change the
driver's behaviour to tidy a log.

Every seam that answers a request is covered: requests, launch, find, resolve,
element actions, page source, and termination.

**The page-source line was argued away and then added, because the argument was
wrong.** A `GET /source` request *is* almost entirely the read, so the request
line does carry the cost — but not the document size, which is the part with
information in it. The numbers that mattered on 2026-08-11 were 19,656 characters
before a click and 37,413 after: a dialog opening, visible as nothing else. A dead
window reports `NO WINDOW` rather than 0, because an empty document is a real
answer for an empty window.

### The console had to be quieted first

`WebApplication.CreateBuilder` wires a console logger at Information, and ASP.NET
emits `Request starting`, `Executing endpoint`, `Writing value of type ... as
Json` and `Request finished` for every request. Measured: **six framework lines
around each transcript line**, which made "console by default, like WinAppDriver"
false as shipped. `builder.Logging.SetMinimumLevel(LogLevel.Warning)` fixes it.

Warning rather than `ClearProviders()`: an unhandled exception is logged through
that pipeline at Error, and silencing it to tidy the output would trade a real
signal for a cosmetic one. Quieting the framework also removed
`Now listening on: ...`, which was the one framework line worth having — so the
host prints its own banner and names where the transcript is going.

### What it found on its first full run

**OBSERVED once, 2026-08-11, not yet explained:**

```
  find AutomationId='num5Button' -> 1 match(es) 54.6 ms
    resolve -> Resolved 32.7 ms
  Click -> Performed via Invoke 43.7 ms
```

The resolve is **75% of the click**, and it is a resolve of an element found
50 ms earlier through `CachingElementResolver` — the layer whose reason for
existing is that a walk costs 19.4 ms and a held handle costs 0.45 ms. 32.7 ms is
neither.

Candidates, none tested: the find and the resolve do not share a cache key, so
the click always misses; the cache is populated on resolve rather than on find,
so the first resolve after any find is always a walk; or the 0.45 ms figure was
measured under conditions this path does not meet.

**This is exactly what the transcript was built for** — the number was always
there and nothing showed it. Do not fix it from this paragraph: it is one
observation, and one observation is not a measurement.

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

---

## The `SendKeysToElement_*` family: measured 2026-08-12, cause narrowed to residue

Recorded before any fix, because three earlier theories about this family were
published and refuted and the raw data is what survives that.

### The control was already in the results, and it is decisive

Three consecutive guest runs (`c5cf728`, `b0de3d8`, `bd3df1f`; no reset, no warm,
cold, Alarms `10.1906.2182.0`):

| family | c5cf728 | b0de3d8 | bd3df1f |
|---|---|---|---|
| `SendKeys_*` (session-level, 12 tests) | **12/12** | **12/12** | **12/12** |
| `SendKeysToElement_*` (element-level, 12 tests) | 8/12 | 10/12 | 8/12 |

**The session-level family has never failed once.** It drives the same keyboard
code, the same `IKeyboardInput`, the same modifier handling and the same drain.
So the typing path is **not** the variable — if it were, both families would move
together.

That retires the three theories this family has already cost:
modifier persistence, a send race, and foreground refusal
(54/55 windows raised). All were about the typing path. The control says the
typing path is fine.

**What differs between the two families is only WHERE the keys land.** The
session route types at the window; the element route resolves an element first
and types at that.

### Which tests fail is different every run, and the messages name residue

`bd3df1f`, verbatim:

```
SendKeysToElement_Alphabet         Expected:<>.          Actual:<Alarm (1)>      dur 7.75 s
SendKeysToElement_SymbolsKeys      Expected:<>.          Actual:<0123456789>     dur 0.04 s
SendKeysToElement_ModifierControl  Expected:<789789789>. Actual:<789>            dur 0.11 s
SendKeysToElement_ModifierAlt      Expected:<False>.     Actual:<True>           dur 0.05 s
```

**`Expected:<>` is the whole finding.** Those tests assert the field is EMPTY
before or after they act, and they find text in it. `0123456789` is another
test's input, not this one's. So the box carries residue across tests.

`Alarm (1)` is not typed text at all — it is an alarm NAME, so that test is
looking at a different control from the one it means, or the app is on a
different page.

`SendKeysToElement_ModifierControl` expected `789789789` and got `789`: a
select-all-and-paste that pasted nothing. That is consistent with the same cause
(the field or the clipboard not in the state the test set up) and is NOT
consistent with keys being dropped, which would lose characters from the `789`
too.

### The 7.75 s duration ties this to a known cluster

`SendKeysToElement_Alphabet` took 7.75 s where its siblings took 0.04-0.11 s.
That is the shape recorded in
`the-8s-cluster-is-a-page-recovery-cascade-not-slowness`: `TestInit()` burning
three full implicit-wait cycles probing automation ids because Alarms is on the
wrong page. **Why the app is on the wrong page was left open there, and this is
the same question arriving from the other direction.**

`TouchScrollOnElement_Vertical` flips on exactly the runs this family flips on
(failed at `c5cf728`, passed at `b0de3d8`, failed at `bd3df1f`), so it is
probably the same cause rather than a separate one.

### Status of the explanation

- **MEASURED** — session-level SendKeys never fails; element-level flaps; the
  failing set changes run to run; the messages show another test's text and an
  alarm name in fields asserted to be empty.
- **HYPOTHESIS** — a preceding test leaves the application on a different page,
  or leaves text in the box, and the next test reads the wrong control or a dirty
  one. Not yet established, and *which* preceding test is not known.
- **REFUTED, do not revisit** — modifier persistence, a send race, foreground
  refusal. The session-level control rules out the entire typing path, not just
  those three mechanisms.

**This is not noise.** The user's framing is the correct one and is adopted here:
there is dirty start state, and there is flake this driver causes. A test that
passes 8 times out of 12 has a cause; the cause is simply not in the code that
was suspected for three rounds.

### The drain is inert in the guest, and that is measurable in the transcript

The `/text` read that fails is **0.9 ms after the keystroke that should have
changed it**, from `transcript-bd3df1f58-184453.log`:

```
18:49:06.553   SendKeys -> Performed via keys 77.5 ms      <- Ctrl+A
18:49:06.564   SendKeys -> Performed via keys 10.8 ms      <- Delete
18:49:06.565   GET .../element/.../text -> 200 jwp 0 0.9 ms
```

`GET /text` goes through `ElementPropertyRoutes.MapRead`, which calls
`DrainTypedInput`, and `POST /value` sets `session.InputPending = true`. So the
drain was armed and cost approximately nothing. Every `/text` in that stretch is
0.8-2.6 ms. **The wait that exists to prevent exactly this race did not wait.**

`SendKeys -> Performed via keys` with **`focused=48, unfocused=0` across the whole
run** — so `SetFocus` never declined once. Focus is not the variable either.

### REFUTED: "WaitForInputIdle waits only once per process"

MSDN says *"subsequent WaitForInputIdle calls return immediately, whether the
process is idle or busy"*, which would have made the drain protect the first read
of a run and nothing after. It predicted that repeated drains against one
long-lived process would read back short.

**Measured and refuted** — `TheDrainWorksMoreThanOnceTests`, six consecutive
type-52-characters-drain-read cycles against one process, **52/52 every time**,
with the fixture spending ~150 ms per cycle. The waits are real and they repeat.
The test is kept: it is the control that makes the next hypothesis falsifiable,
and it pins a documented behaviour that would otherwise have to be re-argued.

### HYPOTHESIS: the drain fails silently on a PACKAGED application

The subject that works is the repository's Win32 app — one process, and the
window handle is the application's own. The subject that fails is Alarms & Clock
behind an `ApplicationFrameWindow`.

`WaitForInputProcessed` returns **`false`** on three paths — the window is gone,
the process id is 0, or `OpenProcess` is denied — and

```csharp
session.InputPending = false;
windows.WaitForInputProcessed(session.WindowHandle);   // result discarded
```

**discards the result**, which violates this repository's own rule that a
return-value error signal must be checked before reading dependent state. So a
drain that never ran is indistinguishable from one that waited.

`OpenProcess` is asked for `PROCESS_QUERY_INFORMATION`, the stronger right;
`PROCESS_QUERY_LIMITED_INFORMATION` is the one that succeeds against
lower-privilege and AppContainer targets. A packaged app is exactly the case
where the stronger right is refused.

**Not yet established.** The next probe asks the guest directly: can this driver
open the hosted Alarms process with those rights, and how long does
`WaitForInputProcessed` actually take there?

### ANSWERED: the drain runs, and it is not a synchronisation primitive

Measured from `transcript-975188db0-191505.log`, the first run with the drain
instrumented:

```
drain WAITED        : 101   (of those, under 1 ms: 80)
drain DID NOT WAIT  : 0
```

**REFUTED: `OpenProcess` denied for a packaged application.** Not once in 101
drains. The AppContainer theory was wrong, and so was the guess that
`PROCESS_QUERY_INFORMATION` is too strong a right — the weaker
`PROCESS_QUERY_LIMITED_INFORMATION` would have changed nothing.

**MEASURED, and this is the actual defect: 79% of drains return in under a
millisecond.** `WaitForInputIdle` answers *"is this process idle"*, and
immediately after `SendInput` the process genuinely **is** idle — the injected
keys are still in the system's raw input queue and have not been posted to the
target thread yet. The wait ran, returned "idle", and waited for nothing.

The guest probe agrees independently: against `charmap`, a plain Win32 GUI
process, `WaitForInputIdle` returned IDLE in **0.0–4.5 ms** immediately after 52
characters were injected.

**So the drain is an accidental short delay, not a synchronisation.** It is still
load-bearing — mutation-removing it fails both host tests, one reading back
**54 of 52** characters — but what it buys is the few hundred microseconds of an
`OpenProcess` plus a kernel transition. That is enough for a Win32 `EDIT` and not
enough for a UWP XAML control, which is exactly the split between the subject
that passes and the subject that flaps.

#### What this rules out, and what is left

| candidate | status |
|---|---|
| modifier persistence | REFUTED (earlier) |
| a send race in the keyboard path | REFUTED — session-level `SendKeys_*` is 12/12 × 3 runs |
| foreground refusal | REFUTED — `focused=48, unfocused=0` |
| `WaitForInputIdle` waits only once per process | REFUTED — 6 repeated drains read 52/52 |
| `OpenProcess` denied for a packaged app | REFUTED — 0 of 101 |
| **`WaitForInputIdle` is the wrong question** | **MEASURED — 80/101 under 1 ms** |

**The fix is not another primitive picked by argument.** This repository's own
rule applies: *synchronise on the condition, never on the clock*. The condition
after typing is the element's own value, and the honest options are:

1. **Verify the write in `POST /value`** — it knows the text it sent, so it can
   wait until the value reflects it, bounded by the reference's budget. Covers
   literal text. Does **not** cover the failing case, which is `Ctrl+A` then
   `Delete`, where the expected value is not derivable from the keys.
2. **Wait for the value to settle** — poll until two consecutive reads agree.
   Handles `Ctrl+A`/`Delete` (`"Alarm (1)"` → `""` → stable) but has the same race
   at the front: two reads taken before the app processes anything also agree.
3. **Wait for the input queue to drain on the target thread** —
   `AttachThreadInput` plus `GetQueueStatus(QS_KEY)`, polled. Previously measured
   as reporting zero pending while 51 keys were queued, but that measurement is
   now suspect for the same reason `WaitForInputIdle` is: it may have been taken
   before the keys reached the thread queue.

None of these should be chosen without measuring, and the measurement has to be
against a **UWP** subject, because a Win32 `EDIT` passes under all of them.

#### Two subjects tried for a XAML measurement, and neither can serve

Evaluating any candidate drain needs a subject where a working drain and a broken
one predict **different** observations. The Win32 one does — mutation-removing the
drain fails `TheDrainWorksMoreThanOnceTests`. Two attempts at a XAML subject did
not, and both failures are worth recording because each looked like a result.

**Calculator: insensitive to its own manipulation.** Typing three digits and
reading the display back passed **with the drain removed**. Three injected
characters land fast enough that waiting and not waiting agree — the effect size
is below the resolution of the condition. Calculator cannot host a larger one
either: its display caps the digits, so a 52-character condition would report
truncation as a race. *This one nearly shipped as "the drain is fine on UWP".*

Its first form was worse: it clicked Calculator's C button between iterations,
the click did not take, and every iteration reported a race that was really an
ineffective setup step. A setup that shares the mechanism under test cannot
isolate it.

**The WPF subject: unstable.** Retargeted to the WPF `TextBox` with 52
characters, the run failed with `NoSuchWindow` — the application lost its window
partway through, so the fixture measured its own subject dying rather than a
race.

**So there is currently no local experiment that can evaluate a candidate fix
against a XAML control.** That is the blocker, and it is worth more than another
guess at the primitive: a fix validated only on the Win32 subject would be
validated by exactly the subject that passes under every candidate.

The available validator is the guest compatibility suite itself, with a stated
prediction — the `SendKeysToElement_*` family stops moving between runs — but a
change to the element read path affects every element command, so it should be
made deliberately and measured cold, not appended to a session's end.

## The one flake in our OWN suite, and why it may be the same defect

`StoppingAnApplicationWithUnsavedWork_LeavesNoPromptAndTakesNoKillWait` failed
twice in six full integration runs on 2026-08-12 and passed in isolation both
times. Order-dependent state, not a bad assertion.

**The failure is FAST — 315 ms and 405 ms** — which rules out most of the test.
Enumerating what can fail that quickly:

| assertion | can it fail in ~400 ms |
|---|---|
| `BringToForeground(window).ShouldBeTrue()` | **yes** |
| `new SendInputKeyboard().Type("unsaved").ShouldBeTrue()` | **yes** |
| `result.Application.ShouldNotBeNull()` | yes, but the launch is otherwise reliable |
| `UiSettle.Until(title starts with '*', 10 s)` | no — it would take 10 s |
| `teardown.Elapsed < 4000 ms` | no — it would take over 4 s |

So the failure is in **acquiring the foreground, or typing right after acquiring
it**. The helper does:

```csharp
_windows.BringToForeground(window).ShouldBeTrue(...);
_windows.WaitForInputProcessed(window);
new SendInputKeyboard().Type("unsaved").ShouldBeTrue();
```

**`BringToForeground` is called once and its success is asserted immediately.**
Nothing waits for the window to actually BE foreground, and Windows restricts
foreground stealing — a subject launched moments earlier, with another fixture's
application still holding the foreground, is exactly the case that fails
intermittently. This repository's own rule applies: synchronise on the condition,
not on a single attempt.

### Why this may not be only a test defect

`UiaElementInteractor.SendKeys` does the same two steps in the same order:

```csharp
_windows?.BringToForeground(window);   // result DISCARDED
bool focused = TryFocus(element);      // recorded
_keyboard.Type(keys);
```

**The foreground result is discarded there too.** Synthesised keys go to whatever
holds the foreground, NOT to a handle — so a failed raise means the keystrokes
landed in another window, and the request still answers 200. That is the same
class of defect as the drain's discarded boolean, in the same method, and it is
currently invisible: the transcript records `SendKeys -> Performed via keys`
whether or not the window ever came forward.

Note what the existing measurements do and do not cover. `focused=48,
unfocused=0` is **`SetFocus`**, a different call — it says nothing about the
raise. The `keys -> raised` counter belongs to the session-level `/keys` route,
which is the family that never fails. **Nothing measures the raise on the element
path, which is the family that flaps.**

**HYPOTHESIS, and the next experiment is cheap:** log the `BringToForeground`
result on the element SendKeys path, exactly as the drain now logs its wait, and
read the next guest run. If raises are failing there, it explains typed text
going missing without any drain being involved — and it would explain why the
session-level family, which raises through a path that already records the
result, is 12/12.

### REFUTED: failed foreground raises on the element path

Measured from `transcript-3362c89a0-195109.log`, the first run with the raise
recorded:

```
element SendKeys raised+focused : 45
element SendKeys NOT RAISED     :  1
element SendKeys unfocused      :  0
```

**The single failure does not belong to the family.** It is at `19:56:05` in the
Action Center session — a shell surface that legitimately owns the foreground —
while the four failing `SendKeysToElement_*` tests ran between `19:55:13` and
`19:55:21`. Correlating the timestamp is what settles it; the count alone
(1 of 46) would have left it arguable.

So the element path raises, focuses and types correctly during exactly the tests
that fail. **Seventh candidate refuted by measurement.**

### Where that leaves it

| candidate | status |
|---|---|
| modifier persistence | REFUTED |
| a send race in the keyboard path | REFUTED — session family 12/12 × 3 runs |
| foreground refusal (session path) | REFUTED — `keys -> raised` |
| `SetFocus` declining | REFUTED — `unfocused=0` twice |
| `WaitForInputIdle` waits once per process | REFUTED — 6 drains, 52/52 each |
| `OpenProcess` denied for a packaged app | REFUTED — 0 of 101 |
| **failed raise on the element path** | **REFUTED — the one failure is another session** |
| **the read races the typing** | **still standing, and now the only one** |

The evidence for the survivor is direct and unchanged: `GET /text` lands
**0.9 ms** after the keystroke that should have changed it, the value it returns
is exactly the pre-typing content (`Alarm (1)`, the new-alarm default, or the
previous test's `0123456789`), and the drain in between returns in under a
millisecond 79% of the time because `WaitForInputIdle` samples before the
injected keys reach the target thread.

**What is missing is not another hypothesis — it is a local subject that can
falsify this one.** A Win32 `EDIT` is too fast, Calculator's condition is too
small and its display caps, and the WPF subject lost its window mid-run. Until
one exists, any drain change would be validated by subjects that pass under every
candidate, which is how the current drain looked correct for two days.

### FIXED: `SendKeysToElement_*` is 12/12 across two independent guest runs

Implemented at `eb1b4c1` / merged at `a085cd6`. `UiaElementInteractor.SendKeys`
now waits for the element's own `ValuePattern` value to stop changing before
returning, instead of leaving that to whichever route runs next. Full mechanism
and the `WaitForValueToSettle` algorithm are documented at the commit; this
entry records the guest confirmation.

```
              c5cf728  b0de3d8  bd3df1f  975188d  3362c89  a085cd6  a085cd6
                                                             (run 1)  (run 2)
SendKeys_*      12/12    12/12    12/12       -        -    12/12    12/12
SendKeysTo...    8/12    10/12    8/12        -    (mixed)  12/12    12/12
Overall score     248      252      254      253      254      257      259
```

**Two consecutive full runs, both 12/12, is the bar this investigation set for
itself.** Every prior measurement of this family — five separate runs — showed a
different failing subset. One clean run was not going to be trusted here; two
were run deliberately before writing this up as fixed, per this project's own
rule that reproducibility only counts when the confound has actually changed.

**`TouchScrollOnElement_Vertical` passed in both runs too.** It had flapped on
exactly the same runs as the SendKeys family throughout this investigation, with
a standing note that it was "probably the same cause." That held.

**One new failure appeared in run 1 that is plainly unrelated:**
`CreateSessionWithWorkingDirectoryAndArguments` — `An element could not be
located`, a session-creation find-timing issue with no code path anywhere near
`SendKeys`. Consistent with the "dirty start state" category already
established for this suite. Not chased further.

**The investigation, start to finish:** eight candidates were proposed across
this and the prior session; seven were refuted by direct measurement rather than
argument (modifier persistence, a send race, foreground refusal on both the
session and element paths, `SetFocus` declining, "`WaitForInputIdle` waits once
per process", `OpenProcess` denial for a packaged app). The eighth — the process-
level drain answering a proxy question instead of the actual property — is the
one that held, and is now fixed at the mechanism it was diagnosed at.

## The multi-request touch drag: interpolation landed, the lift is still unsolved

`TouchDownMoveUp_DragAndDrop` (and `MouseDownMoveUp`, which is a different code
path — see below). Three guest measurements, and the middle one produced a
theory the third refuted.

### What is fixed

`/touch/move` interpolates the path instead of teleporting. The runner tracks
the contact between the three separate requests, keyed by window. Before, a
drag injected one frame jumping 100 px, which no window manager can follow.

### The three measurements

| commit | `/touch/move` | `/touch/up` | drag works |
|---|---|---|---|
| `a085cd6` | ~1 ms, one frame | 200 | no — teleport |
| `7f02766` | **314 ms**, paced over `DragDuration` | **500 jwp 13** | no — lift refused |
| `b2dd934` | 2.2 ms, `TimeSpan.Zero` | 200 | no — whole gesture 4 ms |
| `43d510b` | **414 ms**, paced, no trailing gap | **500 jwp 13** | no — lift refused |

### REFUTED: "the trailing sleep after the last frame kills the contact"

`7f02766` refused the lift, and the pacing loop was found to sleep after *every*
frame including the last — leaving the contact unrefreshed for one interval plus
the HTTP hop. Plausible, and wrong. `43d510b` removed exactly that trailing gap
and the lift was refused again, at 500 jwp 13, identically.

**So the duration is what breaks it, not the gap.** A move of ~2 ms lifts fine;
a move of 300–400 ms does not, whether or not it ends on an injected frame. The
next injected `down`/`up` pair in the same session always succeeds in under a
millisecond, so the injector is not left broken — only the lift that follows a
long move.

`43d510b` also cost three tests (261 → 258: `Launch_ClassicApp`,
`CreateSessionWithWorkingDirectoryAndArguments`, `TouchScrollOnElement_Vertical`)
and was reverted. `b2dd934` remains the best measured state.

### Where to look next, and where NOT to

### ANSWERED: `ERROR_INVALID_PARAMETER`, and it is duration-dependent

The refusal now carries its Win32 cause, and the answer is not what the
timeout theory predicted. From `982eb32`, the compatibility suite's OWN
exception text:

```
TouchDownMoveUp_DragAndDrop
System.InvalidOperationException: The system refused a touch contact (Up)
  (ERROR_INVALID_PARAMETER - the frame was rejected)
```

**`ERROR_INVALID_PARAMETER`, not `ERROR_TIMEOUT`.** The lift frame is rejected
outright rather than the contact being reported as timed out — the shape of
"this frame names a contact that no longer exists".

**The variable is time, and nothing else.** `b2dd934` injected the SAME ten
frames to the SAME destination coordinates with no pacing and lifted cleanly.
Frames, coordinates and window are all controlled; only elapsed time differs.

| move duration | lift |
|---|---|
| ~2 ms (`b2dd934`) | 200 |
| ~156 ms (`6c392f7`) | `ERROR_INVALID_PARAMETER` |
| ~314 ms (`7f02766`) | `ERROR_INVALID_PARAMETER` |
| ~414 ms (`43d510b`) | `ERROR_INVALID_PARAMETER` |

**STOP RE-TUNING THE DURATION.** Three of those four rows are duration changes
and the threshold hunt was abandoned deliberately at `6c392f7` after 100 ms
(measured 156 ms on the guest) failed too. A fifth value is not evidence, and
finding a number that happens to work would leave a magic constant with no
explanation behind it.

### The next experiment, and it is not a duration

**HYPOTHESIS, untested: the contact is thread-affine.** `/touch/down`,
`/touch/move` and `/touch/up` are three separate HTTP requests, and ASP.NET is
free to serve them on different thread-pool threads. If `InjectTouchInput`'s
contact state is per-thread, a contact opened on one thread and lifted on
another is genuinely invalid — which is exactly `ERROR_INVALID_PARAMETER` — and
the duration correlation follows without the OS dropping anything: a fast
gesture is likelier to reuse the same pooled thread, a slow one likelier to
migrate.

That predicts something the duration theory does not: the failure should track
**thread identity**, not elapsed time. Log the managed thread id in the touch
routes and read the transcript — same thread across all three requests when the
lift succeeds, different when it fails. If it holds, the fix is to marshal
injection for a gesture onto one thread rather than to pick a smaller number.

**Do not test this on the host.** It injects real touch at screen coordinates;
it belongs on the guest, and a local probe already had to be abandoned twice —
once for a wrong hand-rolled `POINTER_TOUCH_INFO` (every case, including the
control, failed at DOWN) and once because the guest agent runs Windows
PowerShell 5.1, which cannot load this project's .NET 10 assemblies at all.

### REFUTED: the contact is thread-affine

Measured at `18294d7`. The refusal was made to name both threads when they
differ, so a same-thread result refutes the idea outright — and that is what
came back:

```
The system refused a touch contact (Up) (ERROR_INVALID_PARAMETER - the frame was rejected)
```

No thread clause, meaning the contact was opened and lifted on the **same**
thread. So `ASP.NET` serving the three requests on different pool threads is not
the cause, and thread-marshalling would fix nothing.

**What is now controlled, and refuted, for this one failure:** the error is not a
timeout, the frame count is not it, the coordinates are not it, the trailing
pacing gap is not it, and the thread is not it. Duration remains the only
variable that moves the result, and re-tuning it has been tried three times.

### The next candidate, untested

**A slow drag may put the target into a modal move loop.** `DefWindowProc`
handles a title-bar drag by entering a nested message loop that captures the
pointer. A fast synthetic move may never trip whatever threshold starts it,
while a slow one does — which would explain the duration correlation without any
contact being dropped, and would explain why the window never ends up moved: the
drag begins and the lift that should finish it is refused.

It predicts something checkable that duration alone does not: during a failing
drag the target window should be in a modal loop, observable from outside via
`GetGUIThreadInfo` (`GUI_INMOVESIZE`). That is a real observation rather than
another number to try, and it is where this should resume.

### Older note, superseded above

The remaining question is why a synthetic touch contact cannot survive a long
move.

**Already checked and NOT the answer:** `SyntheticPointer.InjectTouch` uses
`InjectTouchInput` with a one-time `InitializeTouchInjection`, not a device
created per call, and the contact's `pointerId` is a stable `0` across every
frame. So a long move really is one contact being updated, not a burst of
independent ones.

What the measurements pin down: every `Inject` during the move SUCCEEDS — the
move returns 200, and a failed frame would return `The system refused a contact
update` instead. The contact is alive across all ten frames and dies between the
last frame and the lift, and it does so whether that interval is one pacing gap
plus an HTTP hop (`7f02766`) or an HTTP hop alone (`43d510b`).

`InjectTouchInput` has a documented inactivity timeout, but the gaps here are
well inside it and the gaps *during* the loop are the same size as the one
before the lift. That is the contradiction to resolve, and it wants a direct
probe on the guest — inject a down, a paced series of updates, and an up, with
`GetLastError` captured on the failing call — rather than another change to the
driver. `GetLastError` is not currently captured anywhere on this path, which is
why four runs have produced a boolean and no reason.

**Do not simply re-tune the duration.** Two of the four runs above were duration
changes and neither moved the test; a third value is not evidence.

`MouseDownMoveUp` is a SEPARATE defect in a different place —
`SendInputPointer.MoveTo` sends a single absolute jump, with no interpolation at
all. It was deliberately left alone: that is a Platform primitive shared with
the click ladder, so changing it and a touch behaviour in one run would make a
moved score unattributable.

## The touch drag is not a timing problem — measured against the reference

Four probe runs and a direct comparison with WinAppDriver on the same guest, same
window, same gesture. Everything the previous section chased is now closed.

### What the reference does, observably

```
                 /touch/down   /touch/move   /touch/up    window moved
WinAppDriver       199.7 ms      169.8 ms     182.4 ms        YES
this driver          8.6 ms        6.9 ms       1.8 ms         NO
```

The reference spends ~200 ms on **every** phase and holds the contact ~370 ms
across the gesture. Ours completes in 17 ms.

### REFUTED: the contact cannot survive a long gesture

A bare hold — down, wait, up, no move at all:

```
hold ms        0    50   100   200   400   800
ours          ok    ok    ok    ok    ok   REFUSED
reference     ok    ok    ok    ok    ok    ok
```

Ours survives 400 ms. The drag whose lift was refused had a move of **156 ms**.
So the earlier refusals were never a simple lifetime limit, and the reference
holding ~370 ms successfully rules out "long gestures are impossible" outright.

### REFUTED: giving the window manager time fixes it

The reference's shape reproduced exactly — down, 150 ms dwell, one move frame,
150 ms dwell, up:

```
target       move?   up result
client        no       ok
client        yes      ok
title bar     no       ok
title bar     yes      ok

does it MOVE the window?   before x=208 y=87 -> after x=208 y=87   NO
```

**The whole gesture succeeds and the window does not move.** Contact survives,
lift accepted, timing matched to the reference — and nothing drags.

### What that leaves

**The difference is in WHAT is injected, not WHEN.** Every timing hypothesis is
now closed by measurement:

| candidate | status |
|---|---|
| ERROR_TIMEOUT / contact expiry | REFUTED — error is `ERROR_INVALID_PARAMETER`; 400 ms holds fine |
| frame count | REFUTED |
| coordinates | REFUTED |
| trailing pacing gap | REFUTED |
| thread affinity | REFUTED — same thread |
| too fast for the window manager | **REFUTED — reference timing reproduced, still no move** |

Touch injection itself works: taps land, `Touch_Click_*` and
`TouchDownMoveUp_SingleTap` pass. **Only dragging fails.** So a synthetic contact
this driver produces is not being recognised as a window-drag gesture, while the
reference's is.

**The next question is about the frame's contents, not its schedule** — contact
area, pressure, orientation, `touchFlags`, or whether a UWP title bar needs
intermediate samples a single jump never provides. That is a different
investigation from the four already spent here, and it is where this resumes.

**Cost note, recorded deliberately:** this is one suite test
(`TouchDownMoveUp_DragAndDrop`, plus `MouseDownMoveUp` for a separate reason) and
has consumed roughly fifteen probe and guest runs. The refutations are permanent
and the search space is much smaller, but the return per run has been poor and
that is worth weighing before the next one.


### REVERTED: starting the pointer at the viewport origin

`Pen_Click_OriginPointer` and `Touch_Click_OriginPointer` fail with this driver's
own guard refusing a point:

```
(101,33) is outside the application window, so the input was not dispatched
```

The reasoning looked sound. W3C starts a fresh pointer at (0,0) **of the
viewport**, the viewport here is the window, and the suite feeds a
window-relative `element.Location` into a pointer-origin move — so the
accumulated position, held in screen coordinates, should start at the window's
top-left rather than at a raw (0,0). A `viewport` origin already converted
correctly; a `pointer` origin added its offset to zero and produced a desktop
coordinate.

**Measured, and it did not work.** Score went 262 → 259:

- The two target tests still failed, at **(115,87)** instead of (101,33) — the
  arithmetic moved and is still wrong, so the premise is incomplete at best.
- `ActionsError_NoSuchWindow` **regressed**: the early window-placement call now
  fires first and answers *"The session window could not be placed, so a viewport
  coordinate has no meaning"* where the suite asserts *"Currently selected window
  has been closed"*.

Reverted at `9f768cb`. The W3C reading may still be right — a correct change that
is insufficient looks identical to a wrong one from the score alone, and this one
also carried a message regression. What it is NOT is a fix, and keeping it for
the reasoning while it costs three tests would be keeping a story rather than a
result.

**Before trying again:** find out where (115,87) comes from. If the window is at
x=208, a start at the window's left cannot produce x=115, so either
`WindowOrigin(window, 0, 0)` does not return the window's top-left or the offsets
are not what the suite is assumed to send. That is one transcript line away and
was not checked before committing — which is why this cost a run.


## The drag: what actually works, and what does not

**DRAGGING WORKS.** This was obscured for four investigations by looking only at
the failing test. Measured at `538f908`:

| test | path | outcome |
|---|---|---|
| `Touch_DragAndDrop` | `/actions` | **Passed** |
| `Pen_DragAndDrop` | `/actions` | **Passed** |
| `Touch_Scroll_*`, `Pen_Scroll_*`, `Touch_Flick`, `Pen_Flick` | `/actions` | **Passed** |
| `TouchDownMoveUp_SingleTap` | `/touch/*` | **Passed** |
| `TouchDownMoveUp_DragAndDrop` | `/touch/*` | **Failed** |
| `MouseDownMoveUp` | `/moveto` + `/click` | **Failed** |

So the capability exists and is proven. **Only the multi-request path fails**, and
only when it drags — a tap on the same path works.

### The difference, narrowed

`/actions` receives down, move and up in ONE request and injects them in one
continuous loop. `/touch/down|move|up` receives three requests.

- **Unpaced** (`TimeSpan.Zero`): the lift succeeds, the window does not move.
- **Paced** (`DragDuration`): the lift is refused with
  `ERROR_INVALID_PARAMETER`, and the window does not move.

Both use the SAME `Move()` with the SAME interpolation. `/actions` paces frames
~100 ms apart (1000 ms ÷ 10 frames) and works; the multi-request path paces them
~32 ms apart and fails — so it is not the frame rate either.

### REFUTED, properly this time: thread affinity

An earlier check reported threads only WHEN THEY DIFFERED, found none, and
retired the idea. That was a negative from an instrument that only sometimes
looks. Re-instrumented to report all three phases always:

```
The system refused a touch contact (Up) (ERROR_INVALID_PARAMETER)
  [down on thread 7, moved on 9, lifting on 7]
```

**Down and lift on the SAME thread, and the lift is still refused.** A dedicated
single-threaded injection pump was then built so every frame of every gesture
goes down one thread — and the drag still failed identically. Both the diagnostic
and the fix say the same thing, so this is closed. The pump was reverted rather
than kept: it did not do what it was built for, and keeping it would be keeping
unproven complexity.

### What is left

A bare hold — down, wait 400 ms, up, crossing two request boundaries — **works**.
A paced move of 320 ms between the same two boundaries **fails**. The contact
survives silence but not injected UPDATE frames, and no timing, threading or
frame-rate explanation survives contact with both facts.

**The pragmatic option, not yet taken:** the `/touch/*` trio describes a gesture,
and nothing in the protocol requires each phase to be injected at the instant its
request arrives. Recording down and move as intent and replaying the whole
gesture on `up` would use the `/actions` code path that already passes. It is a
real semantic change — a client that presses, observes, then lifts would see
nothing in between — and it should be a deliberate decision rather than a quiet
one, which is why it is written down here instead of done.

**Cost, recorded honestly:** roughly twenty probe and guest runs across two
sessions for two suite tests. What that bought is a much smaller search space and
five permanent refutations, but the return per run has been poor and the next
attempt should start from the table above rather than from a fresh theory.

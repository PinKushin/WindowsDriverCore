# WindowsDriverCore — Consolidated Project Knowledge

**Exported 2026-08-08, outside the repo, so it survives a from-scratch rewrite.**

This is everything learned across ~2 weeks and one long research session, condensed. It replaces
`docs/memory/001`–`016`. Read it before writing any code. It is written for someone who has read
the WinAppDriver docs but was not present for any of the debugging.

**How to use it:** sections 1–4 are the specification. Sections 5–7 are implementation knowledge
that was expensive to acquire. Section 8 is a list of mistakes not to repeat — both in the code and
in the reasoning.

---

## 0. The one lesson, if you read nothing else

**Five load-bearing claims in this repository turned out to be wrong. Every one
was inherited from an earlier session and repeated without being checked.**

**None of them were the user's.** The project `CLAUDE.md` was written by earlier
AI sessions, so its technical assertions carry no more authority than any other
file here — the user confirmed this explicitly on 2026-08-08 about the caching
rule below. Its *process* rules (test-first, no `var`, zero warnings, branching)
are a different matter and stand.

| Claim | Reality | Cost |
|---|---|---|
| "#857: elements are absent from the tree WinAppDriver searches" | They are absent from the UIA tree entirely. Inspect.exe cannot see them either. Not fixable by any client. | The project claimed a fix it cannot deliver |
| "#1079: FindElements randomly returns empty" | Deterministic `FindElement`/`FindElements` disagreement over the same XPath | An experiment was built against a condition unrelated to the bug |
| "Both come from the managed wrapper's cached view" | Appears in neither issue. Inference presented as fact — and the entire justification for the architecture | Two weeks of confident repetition |
| "The Alarms fixture fails because X" | Four successive wrong answers before measurement found a renamed control | A day |
| "Never hold an element between calls — it is a snapshot that drifts" | A held `IUIAutomationElement` is a **live proxy**. Properties come from the provider on every read, its runtime id survives tree changes, and a destroyed element throws `UIA_E_ELEMENTNOTAVAILABLE` instead of answering. Measured in `HeldElementLivenessTests` | A full tree walk on **every** element command, and the belief that FlaUI had a structural speed advantage here |

A sixth was caught going the other way: overstating WinAppDriver's fault for
something that was really a consequence of building custom MAUI controls out of
primitives. **Overstating in the project's favour is the failure mode to watch
for most**, because nothing in the repo pushes back on it.

Every correct answer this project has came from running something: recording real
responses, walking a live UIA tree, driving a real app step by step. Every wrong
one came from reading a summary and reasoning forward.

### The mistake that keeps recurring, in four disguises

Every wrong claim in the table above is the same error underneath: **an
observation for which the correct and the broken implementation predict the same
thing.** It is worth naming, because it has now appeared four times in different
clothes and was not recognised as the same thing until the third.

These are not a new taxonomy. They are the **wrong condition** and **wrong
instrument** routes from the testing standard in the global `CLAUDE.md`, which
already listed both — the failure was not having a name for them, it was not
checking against the names that existed.

| Where | Route | The blind observation | What discriminated |
|---|---|---|---|
| `tag name` locator comment | wrong condition | The example `"Button"` — a Button's localized type differs from its programmatic name only by case | `ListItem` against `list item` |
| `Format_IsInjective` property test | wrong condition | Two independently generated runtime ids never share a digit concatenation, so the region where the hypotheses differ has measure zero | Construct the colliding pair: the same digits partitioned two ways |
| `/location` bounds test | wrong instrument | Two elements have the *same* offset — which is `0 == 0` once the subtraction is removed | Assert the offset is also **non-zero** |
| Cache eviction test | wrong instrument | "Is the kept element cached at the end?" — under first-in-first-out it is evicted, re-resolved and re-added, so it usually is | Count how many times the inner resolver was asked for it |

The two instrument failures share the shape of the standard's own leak-test
example, where a search of serialized *bytes* stood in for *content* and escaping
decoupled them. Presence in a cache stands in for never having been evicted, and
re-insertion decouples them. **A proxy tracks the variable faithfully right up
until the bug is the thing that separates them**, which is why it survives review
and dies to a mutation.

The standard also warns that the instinct is to strengthen the assertion and that
this is usually wrong. True in three of the four: strengthening would have fixed
none of them. The fixes were to **change the input** — `ListItem`, constructed
collisions — and to **change what is measured** — a non-zero offset, a count of
walks.

Three of the four were **written specifically to catch the bug they missed**, and
all four passed against a deliberately broken implementation. Randomising the
input does not help; two of them generated thousands of cases.

**The question to ask before writing the assertion is not "what should be true"
but "is there an input where correct and broken differ, and does my observation
see it?"** Sabotage the code and watch the test fail; a test that stays green
under sabotage is measuring something else, however well its name reads.

### Under load, suspect load before mechanism

A loaded machine makes timing-derived diagnoses unreliable, and this project has
now produced four instances in one session:

- A geometry test that failed roughly one run in two, blamed on a coordinate bug
  that had already been fixed. It was a window still settling.
- A find benchmark that read 11.9 ms and 16.5 ms across consecutive runs with no
  code change.
- Mutation runs, where a busy machine turns real survivors into false
  `Timeout`s — the score comes out *wrong*, not merely slow.
- A 67-second server startup elsewhere, diagnosed as a port collision. There was
  no collision; the port was already deliberately non-default. It was load.

**The shape is always the same:** a slow or intermittent observation acquires a
mechanical explanation, because a mechanism is satisfying and "the machine was
busy" is not. The mechanism then gets fixed, or worse, designed around.

Before theorising about a mechanism from a timing observation, ask what else was
running. It is one question and it is free.

**Better than a habit: gate on setup duration.** From the sibling project, whose
Appium setup runs **9.7 s healthy and 67 s when the machine is loaded**, and
where the slow case reliably precedes a poisoned run. Setup is the fragile
window — an emulator booting or another suite starting is enough — and its
duration is a leading indicator *regardless of the cause*, which is exactly what
makes it useful. You do not have to know what the load was.

`FindBenchmarks` now refuses to report if setup exceeds ten seconds, because for
a benchmark **load does not make a number slow, it makes it wrong**, and a wrong
number that looks plausible is worse than no number. The same logic applies to
mutation runs, where load converts real survivors into false timeouts.

**The diagnosis that produced this was wrong twice**, and the author's own note on
why is worth keeping: both times they reasoned from a plausible mechanism instead
of reading the setup code, which took one grep. Same failure as everything else in
this section — a satisfying explanation preferred to a cheap observation.

**A claim written in this repository is not evidence.** The recordings, the
measured numbers and `LIMITATIONS.md` were produced by running something.
Treat everything else as a hypothesis until it has been — `CLAUDE.md` included.

The fifth entry shows the shape this takes when it survives longest: a rule
phrased as an engineering principle, in the file that reads most like authority,
justified by two of the other wrong claims. It cost a tree walk per command and
was refuted by one experiment that took minutes to write. **Doctrine is the
easiest kind of claim to inherit, because it does not look like a claim.**

---

## 1. What this project is

**The WinAppDriver API, implemented on raw `IUIAutomation` COM, built to FlaUI's standard of
capability.**

- **FlaUI reaches UIA properly** — pattern-aware, and explicit about the difference between
  invoking a pattern and dispatching real mouse input.
- **But it is a .NET library.** No Appium suite, no Python test, nothing speaking WebDriver can
  drive it. Reaching for FlaUI means leaving the protocol behind.
- **WinAppDriver has the API every existing suite already speaks** — and an implementation that is
  weak, and unmaintained since April 2025. NOT archived — that claim was wrong.

So: serve WinAppDriver's protocol over a UIA layer as capable as FlaUI's. An existing suite points
at it unchanged and stops hitting the ceiling.

This is why the only dependency is `Interop.UIAutomationClient` — FlaUI's own interop layer, the
raw COM surface — and deliberately **not** `FlaUI.Core`. A peer, not a wrapper. Taking the wrapper
would inherit its abstractions and defeat the point; taking its interop layer is just using the
COM definitions someone already wrote correctly.

**Protocol compatibility is the floor, not the ceiling.** Where WinAppDriver's behaviour is a
defect rather than a contract, match the protocol and fix the behaviour. Where a capability has no
expression in the protocol, add a vendor extension rather than silently choosing for the caller.

The founding motivation was two WinAppDriver bugs. **Both were misdescribed in this repository
for two weeks — read `docs/FOUNDING-PREMISE.md` before relying on anything about them.**

- **#857** — elements visible on screen are absent from the UIA tree. **Inspect.exe cannot see them
  either**, which means the application's UIA provider has not published them and *no* client can
  find them. **This driver does not fix #857 and cannot.**
- **#1079** — not random emptiness. `FindElement` succeeds and `FindElements` returns empty for the
  *same XPath*, deterministically. That is a defect in WinAppDriver's own XPath evaluation, which
  has separate singular and plural paths. Real, probably fixable, and untouched here because XPath
  is not implemented.

The "managed wrapper's cached view" explanation appears in neither issue. It was inference,
repeated as fact in every document here. Querying live remains the right default — it is simpler,
measurably faster, and avoids a class of staleness — but it was adopted for a reason that turned
out to be unsupported.

### As close to the metal as possible

Standing direction, and it decides arguments rather than flavouring them. Nothing sits between
this code and the API it drives unless it is pulling its weight.

- **Raw COM interfaces, not managed wrappers.** A wrapper's behaviour becomes this driver's
  behaviour and its bugs become ours to explain.
- **Own COM lifetime explicitly** — deterministic release, not finalizer roulette.
- **No hidden behaviour**: no implicit retries, no caching the caller did not ask for, no exception
  translation that loses the original. Where the driver decides something for the caller, the
  decision is documented and, where it matters, selectable.
- **Round trips are the cost that matters**, not codegen — this is cross-process COM. If a find
  shows up in a benchmark the answers are `IUIAutomationCacheRequest`, to fetch more per trip, and
  holding the element the caller already has an id for. **Not** a retained *result set* or a cached
  *property snapshot* — those do go stale. A live element reference does not; see §0.
- **`unsafe` where it genuinely pays**, which so far means `Platform`, because that is what the
  P/Invoke source generator emits.

**FlaUI is the capability benchmark, not a dependency.** `Interop.UIAutomationClient` — the only
dependency here — is authored by *Roemer*, FlaUI's author, and **is** FlaUI's interop layer, so the
raw COM definitions are already shared. Taking `FlaUI.Core` on top would inherit its object model
and abstractions, which is the opposite of the point: this project needs to own tree traversal,
COM lifetime and activation strategy in order to expose them through a protocol.

What to take from FlaUI is the *standard* — pattern awareness, and an explicit distinction between
invoking a pattern and dispatching real mouse input. What not to take is the wrapper.

It is also the natural benchmark subject: in-process, no transport, so it measures the floor of
what this work costs. See H3 in `REWRITE-SPEC.md`.

### Why C# rather than C, and how to settle it if it comes up again

The temptation is real — there is no UI here, and the original WinAppDriver was C++. But the one
measurement available already argues against it:

**WinAppDriver *is* native C++, and it is the thing taking ~1070 ms per find against this C#
implementation's ~33 ms.** Native did not save it. The gap is architectural — what it does per
call — not language.

That matches the shape of the workload. A UIA `FindAll` is an RPC into the target application's
provider process; the provider does the tree walk while this process waits. Marshalling overhead on
the client side is noise beside a cross-process round trip, and marshalling is most of what C would
buy back.

Against that, C# is carrying real weight on a component that **takes HTTP input from the network
and launches processes with it**: Kestrel, `System.Text.Json`, and memory safety on exactly the
surface where it matters. The previous implementation shipped a command-injection vector through
`Process.Start`; that class of defect is cheaper to avoid than to audit for.

Where C# could genuinely cost: per-call interop marshalling, GC pause consistency (latency tails
rather than throughput), and JIT warmup — the last irrelevant for a long-running server.

**If a shim is ever warranted, it is C, not Zig.** Zig is the better language on safety — bounds
checks, defined overflow, explicit allocators — but the thing being shimmed is COM, and Zig has no
COM support. `@cImport` chokes on the vtable macros in `UIAutomationClient.h`, so every interface
would be a hand-written `extern struct` of function pointers. A wrong slot ordinal there is silent
memory corruption, which is worse than anything the bounds checking buys back. C inherits the
correct vtable layout from the header for free. Getting COM right is the hard part; memory safety
in a small flat-buffer shim is the easy part.

And the shim's interface should be *"find, and return N elements' properties as one flat buffer"* —
one P/Invoke, one allocation, no RCWs. Not "wrap COM in another language". Note that is the same
idea as a cache request, one layer down, which is why the managed version comes first.

**Do not re-argue this. Measure it.** Instrument a find to separate time spent inside the UIA call
from time spent in managed code. If UIA is 95%+ of it, the question is closed and a rewrite would
be buying the remainder. If the managed side turns out to be material, that is a real finding, and
a C shim for the hot path becomes worth costing — not a whole-project rewrite.

**Native AOT is out.** `Marshal.ReleaseComObject` and built-in COM interop are unsupported under
AOT; getting there means a `ComWrappers` rewrite. JIT is also right on merit — this workload is
dominated by cross-process COM round-trips, not codegen. If AOT ever became a hard requirement it
would mean changing language (C is the lower-effort path than Zig here, because
`UIAutomationClient.h` *is* the ecosystem and Zig's `@cImport` chokes on COM vtable macros).

---

## 2. The protocol contract

### It is JSON Wire Protocol, stated explicitly

`WinAppDriver/Tests/WebDriverAPI/README.md`:

> These tests are written to verify each API endpoint behavior and error values as specified in
> JSON Wire Protocol document.

Where JWP and W3C disagree, **JWP wins**. The previous implementation was built from the W3C spec
and that single wrong choice produced a large fraction of its failures.

### The complete route table (`Docs/SupportedAPIs.md`, 60 rows)

```
GET    /status
POST   /session
GET    /sessions
DELETE /session/:sessionId
POST   /session/:sessionId/appium/app/launch
POST   /session/:sessionId/appium/app/close
POST   /session/:sessionId/back
POST   /session/:sessionId/forward
POST   /session/:sessionId/buttondown
POST   /session/:sessionId/buttonup
POST   /session/:sessionId/click
POST   /session/:sessionId/doubleclick
POST   /session/:sessionId/moveto
POST   /session/:sessionId/element
POST   /session/:sessionId/elements
POST   /session/:sessionId/element/active
GET    /session/:sessionId/element/:id/attribute/:name
POST   /session/:sessionId/element/:id/clear
POST   /session/:sessionId/element/:id/click
GET    /session/:sessionId/element/:id/displayed
GET    /session/:sessionId/element/:id/element
GET    /session/:sessionId/element/:id/elements
GET    /session/:sessionId/element/:id/enabled
GET    /session/:sessionId/element/:id/equals
GET    /session/:sessionId/element/:id/location
GET    /session/:sessionId/element/:id/location_in_view
GET    /session/:sessionId/element/:id/name
GET    /session/:sessionId/element/:id/screenshot
GET    /session/:sessionId/element/:id/selected
GET    /session/:sessionId/element/:id/size
GET    /session/:sessionId/element/:id/text
POST   /session/:sessionId/element/:id/value
POST   /session/:sessionId/keys
GET    /session/:sessionId/location
GET    /session/:sessionId/orientation
GET    /session/:sessionId/screenshot
GET    /session/:sessionId/source
GET    /session/:sessionId/title
POST   /session/:sessionId/timeouts
POST   /session/:sessionId/touch/click
POST   /session/:sessionId/touch/doubleclick
POST   /session/:sessionId/touch/down
POST   /session/:sessionId/touch/up
POST   /session/:sessionId/touch/move
POST   /session/:sessionId/touch/flick
POST   /session/:sessionId/touch/longclick
POST   /session/:sessionId/touch/scroll
DELETE /session/:sessionId/window
POST   /session/:sessionId/window
POST   /session/:sessionId/window/maximize
GET    /session/:sessionId/window/size
POST   /session/:sessionId/window/size
GET    /session/:sessionId/window/:windowHandle/size
POST   /session/:sessionId/window/:windowHandle/size
GET    /session/:sessionId/window/:windowHandle/position
POST   /session/:sessionId/window/:windowHandle/position
POST   /session/:sessionId/window/:windowHandle/maximize
GET    /session/:sessionId/window_handle
GET    /session/:sessionId/window_handles
```

Note what is **absent**: no `/window/rect`, no `/element/:id/rect`, no `/window/minimize`, no
`/window/restore`. The old implementation invented all of those and skipped the underscore forms.

### `window_handle` is the highest-leverage single route

`Tests/WebDriverAPI/AppSessionBase/Utility.cs:47`, `CurrentWindowIsAlive`, is written so that a
line unconditionally overwrites the computed result with `true`. The **only** path to `false` is
`remoteSession.CurrentWindowHandle` throwing. Selenium 3.8 sends
`GET /session/:id/window_handle`. Miss it and the client throws, the suite decides the session is
dead, and it tears down and relaunches the app on **every** fixture init across all 290 tests.

Only `AlarmClockBase` calls it; `CalculatorBase` checks `session == null` instead. Calculator
relaunches once per test *class* by design — 35 of 43 classes have
`[ClassCleanup] → TearDown() → session.Quit()`.

### Response envelopes — recorded, not inferred

**All of this is measured** from WinAppDriver 1.2.2009.02003 on Win11 26200 and checked in at
`tests/WindowsDriverCore.Tests.Protocol/Recordings/winappdriver-responses.json` (40 records).
That file is the contract. Do not hand-edit it; re-record it.

**Success** carries `sessionId`; **errors do not**:

```json
{"sessionId":"…","status":0,"value":{"ELEMENT":"42.19466560.4.73"}}
{"status":7,"value":{"error":"no such element","message":"An element could not be located on the page using the given search parameters."}}
```

`GET /status` is unwrapped — no `value`, no `status`:
```json
{"build":{"revision":"2003","time":"…","version":"1.2.2009"},"os":{"arch":"amd64","name":"windows","version":"10.0.26200"}}
```

**Status codes actually emitted**, with their HTTP status:

| `status` | `error` | HTTP | Trigger |
|---|---|---|---|
| 0 | — | 200 | success |
| 7 | `no such element` | 404 | find miss, malformed tag name, unknown element id |
| 9 | `unknown command` | 404 | unrecognised route |
| 13 | `unknown error` | 500 | app path not found on session create |
| 19 | `XPath Lookup Error` | 500 | invalid XPath (note the spaces and capitals) |
| 23 | `no such window` | 400 | bad window handle, bad `appTopLevelWindow` |
| **100** | `invalid argument` | 400 | negative timeout ms, missing capabilities |
| **101** | `invalid session id` | 404 | unknown session |

**100 and 101 are not in Selenium's `WebDriverResult` enum** (which uses 61 and 6). WinAppDriver
emits its own. Match WinAppDriver, not the enum.

### 501 responses are plain text, not JSON

This is the finding that explains a string nobody could account for:

```
HTTP 501
Unimplemented Command: css selector locator strategy is not supported
```

No envelope, no JSON. The client cannot parse it, so it **prepends "Unexpected error. "** — which
is exactly why `ErrorStrings.UnimplementedCommandLocator` reads
`"Unexpected error. Unimplemented Command: {0} locator strategy is not supported"`. Emit the bare
text with HTTP 501 and the client composes the rest. Emitting JSON here would produce a different
client-side message and fail the test.

Applies to: unsupported locator strategies (`css selector`, `link text`, `partial link text`) and
unsupported timeout types (`page load`, `script`).

Similarly, `ErrorStrings.XPathLookupError` is `"Invalid XPath expression: {0} (XPathLookupError)"`
but the **server** sends only `Invalid XPath expression: {expr}`. The client appends
` (XPathLookupError)` — the `error` string with spaces removed. Do not send the suffix.

### Other measured behaviours

- `FindElements` with no match → **HTTP 200, `"value":[]`**, not an error.
- Element ids are **dot-separated** (`42.19466560.4.73`), matching the docs. The previous
  implementation used commas.
- `window_handle` returns a hex string with the `0x` prefix and uppercase digits: `"0x01BF112C"`.
- `element/{id}/name` returns the tag name as `"ControlType.Button"`.
- `element/{id}/size` serialises **height before width**.
- `element/{id}/rect` is **501**. It is W3C-only; WinAppDriver does not implement it.
- `element/{id}/clear` on an element with no ValuePattern returns **200**, not an error.

### `SetWindowPos` and `GetWindowRect` disagree by a constant

Measured 2026-08-09 on a Calculator window: setting x to `GetWindowRect`'s
`Left + 120` produced a reported `Left` **175** greater. A constant 55px offset,
because `GetWindowRect` reports the frame including the invisible resize border
while `SetWindowPos` positions something else.

The consequence for tests: **never predict an absolute position from a reading
taken with a different API.** Move to a known place, move again by a known
delta, and compare the two readings — identical operations, so the offset
cancels. Predicting one from the other does not work and produces a failure that
looks like environmental interference.

This is the same class of mistake as mixing UIA bounding rectangles with
`GetWindowRect`, which is why `WindowRelativeBounds` takes both of its
rectangles from UIA.

### Element geometry uses two different coordinate spaces

Measured on one element, read back to back, 2026-08-08:

| Reading | Value |
|---|---|
| `/location` and `/location_in_view` | `{x: 203, y: 419}` |
| `/size` | `{width: 97, height: 35}` |
| `/attribute/BoundingRectangle` | `Left:257 Top:616 Width:97 Height:35` |
| `/attribute/ClickablePoint` | `305,633` |
| `/window/current/position` | `{x: 54, y: 197}` |

`257 − 203 = 54` and `616 − 419 = 197` — exactly the window origin. So
**`/location` is relative to the session window**, while `BoundingRectangle`,
`ClickablePoint` and all synthesized input are in **screen** coordinates. `/size`
is unaffected, being a difference.

This matters for the click ladder: feeding `/location` to `SendInput` is off by
the window origin, and is invisible on a window at the top-left of the primary
display. A guard written against the wrong space passes while the click lands
somewhere else.

The first attempt to explain the disagreement guessed DPI scaling. The ratios did
not match (`257/203 = 1.27` against `616/419 = 1.47`), and asking the window where
it was settled it in a single request. Cheaper than the theory.

### `/text` is ValuePattern first, then Selection, then Name

Calculator cannot show this — every element there has a Name and no ValuePattern,
so "value if available" and "always name" predict the same answer. Settings'
search box has both, and starts empty:

| Subject | Name | Value | `/text` |
|---|---|---|---|
| Settings search box (Edit) | `Search box, Find a setting` | *(empty)* | `""` |
| …after typing `printers` | unchanged | `printers` | `printers` |
| Settings minimize button (control) | `Minimize Settings` | no ValuePattern | `Minimize Settings` |

An **empty** value beating a non-empty Name is the decisive part: the rule keys on
pattern availability, not on whether the string is empty. The button in the same
window at the same moment is the control that rules out a session-wide or
window-wide explanation.

The Selection rung — a List returning its selected item's value — comes from
Microsoft's own `ElementText.GetElementText` (`MinuteLoopingSelector.Text == "00"`),
not from a measurement here.

Note a divergence between two routes reading the same property: `/text` returns
`""` for an empty value, while `/attribute/Value.Value` returns `null`.

### `/attribute/{name}` is an open UIA surface, not a fixed list

28 names measured. Strings verbatim; booleans as `"True"`/`"False"`; integers as
decimal strings; `RuntimeId` dot-separated; `ClickablePoint` as `"305,633"`;
`BoundingRectangle` as `"Left:257 Top:616 Width:97 Height:35"`; `ControlType` as
`"ControlType.Button"` and `LocalizedControlType` as `"button"`; pattern
availability as `Is{Pattern}PatternAvailable`; pattern-qualified names work
(`SelectionItem.IsSelected`, `Value.Value`, `Value.IsReadOnly`).

An **unset** property and an **unknown** attribute name both return `null` with
HTTP 200 — indistinguishable to a caller. An **empty** attribute name is 400,
status 100, `"Attribute command takes exactly one argument namely the attribute name"`.

### A caution about how these were obtained

The first recording pass reported **every error body as empty**, and it was wrong. PowerShell 7's
`Invoke-WebRequest` throws on non-2xx, and `$_.Exception.Response` is an `HttpResponseMessage` with
no `GetResponseStream()`, so the body reader silently produced "". Re-recorded with
`-SkipHttpErrorCheck`, which returns the response instead of throwing.

An earlier version of this document asserted "unknown session → bare HTTP 404, empty body" on the
strength of that broken instrument. It is false. **When a measurement says a server returns
nothing, suspect the client first.**

Canonical error strings also live in `Tests/WebDriverAPI/AppSessionBase/CommonTestSettings.cs`,
class `ErrorStrings` — but note those are the strings **as the client sees them**, after any
prefixing. The recordings are what the server sends.

### Locator strategies

| Client API | Strategy | Matches |
|---|---|---|
| FindElementByAccessibilityId | `accessibility id` | AutomationId |
| FindElementByClassName | `class name` | ClassName |
| FindElementById | `id` | RuntimeId |
| FindElementByName | `name` | Name |
| FindElementByTagName | `tag name` | **ControlType programmatic name, case-sensitive, unprefixed** |
| FindElementByXPath | `xpath` | Any |

Two traps:

1. `tag name` matches the **ControlType** enum's programmatic name — `Button`,
   `ListItem`, `Text` — case-sensitively and *without* the `ControlType.` prefix.
   Measured 2026-08-08: `Button` 200 / `button` 404, `ListItem` 200 / `list item`
   404, `ControlType.Button` 404.

   An earlier version of this table said LocalizedControlType, and this file
   argued for it. **It was wrong, and the reason it survived is instructive:**
   the example used to justify it was `"Button"`, an input where the two
   hypotheses predict the same observation, because a Button's localized type
   differs from its programmatic name only by case. `ListItem` versus
   `list item` is the input where they differ, and it takes one request to
   settle. A confidently-worded comment is not a measurement.

   The prefixed form is real, but it belongs to a different route — `/name`
   returns `ControlType.Button`. Locator unprefixed, tag-name response prefixed.
2. An unknown tag name **must throw**. The old code fell back to `UIA_CustomControlTypeId`, which
   can silently succeed. `Element.cs:142` (`FindElementError_NoSuchElementByTagName`) and `:156`
   (`…ByTagNameMalformed`) both require an exception. This is part of the 23
   "Assert.Fail failed. Exception should have been thrown" failures.

XPath error format, matched exactly: `Invalid XPath expression: {expr} (XPathLookupError)`

`link text` and `partial link text` are unsupported and must throw
`Unexpected error. Unimplemented Command: {strategy} locator strategy is not supported`.

### Capabilities

`app` (an AUMID, a full exe path, or the literal `"Root"` for a desktop session), `appArguments`,
`appTopLevelWindow` (hex string, e.g. `0xB822E2`), `appWorkingDir` (classic apps only),
`platformName`, `platformVersion`.

### Command line

```
WinAppDriver.exe                       # 127.0.0.1:4723
WinAppDriver.exe 4727                  # port only, single argument
WinAppDriver.exe 10.0.0.10 4725        # IP + port
WinAppDriver.exe 10.0.0.10 4723/wd/hub # base path rides on the PORT argument
WinAppDriver.exe * 4723                # bind all interfaces
```

Administrator is required **only** for a non-default IP/port; loopback runs unelevated (verified).

Serve the base path with a single `UsePathBase` configured at startup — **not** by dual-mounting
routes. WinAppDriver listens in one place, decided by the argument. Matching that is fidelity;
dual-mounting is a different behaviour.

---

## 3. Compatibility floor and packaging

Vendor's own statement (`Docs/FAQ.md`): Windows 10 Home/Pro and **Windows Server 2016**.
`Tests/WebDriverAPI/README.md`: "Windows 10 version 1607 or later."

Our API usage matches that floor exactly:

| API | Introduced | Guarded? |
|---|---|---|
| `IUIAutomation` (UIA 3.0) | Windows 7 | — |
| Toolhelp32 process enumeration | Windows 2000 | — |
| `IApplicationActivationManager` | Windows 8 | yes |
| `GetDpiForWindow` | Windows 10 1607 | yes → scale 1.0 |
| `SetProcessDpiAwarenessContext` | Windows 10 1703 | yes → no-op |

.NET 10 supports Windows 10 1607+ and Windows Server 2012+, i.e. **further back than WinAppDriver
goes**. The framework is not the constraint.

Packaging decisions:

- Drop `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />`. It exists only to get
  `System.Drawing` for screenshots and drags WPF + WinForms into a headless HTTP server. Use the
  `System.Drawing.Common` package; the Desktop Runtime prerequisite then disappears.
- Publish self-contained for `win-x64`, `win-x86`, `win-arm64`. WinAppDriver was a single MSI with
  no prerequisites; self-contained is how we match that.
- GitHub runners are `windows-2025` (= `windows-latest`), `windows-2022`, `windows-11-arm`.
  **`windows-2019` no longer exists.** `windows-11-arm` makes arm64 a first-class target — an area
  to beat WinAppDriver, which only ever shipped x86/x64.
- We need no MSI, no admin install, and **no Developer Mode** (WinAppDriver requires it). Say so in
  the README; on a hosted runner it is one less setup step.

---

## 4. Measured ground truth

Full `WebDriverAPI` suite (290 tests) run against **real WinAppDriver 1.2.2009.02003** on Windows
11 26200, 2026-08-08, 28.6 minutes:

| | Passed | Failed |
|---|---|---|
| WinAppDriver (control) | **112** | 178 |
| Old implementation | 77 | 213 |

Cross-tabulated:

| WinAppDriver | Ours | Count | Meaning |
|---|---|---|---|
| Passed | Failed | **70** | the real backlog |
| Passed | Passed | 42 | already working |
| Failed | Failed | 143 | unmeasurable — Alarms fixture defect |
| Failed | Passed | 35 | artifact of our per-test relaunch bug, not wins |

**"80/290 = 27.6%" was never a meaningful number, and neither was 112/290.**
Measured 2026-08-10 in a Windows 10 22H2 guest, WinAppDriver scores **281/290** on
the same suite, from the same binary, with the Windows 11 accommodations still
stashed. Three of the nine failures are a missing UWP package (`0x80073CF1`) and
two need an absent browser.

So 112 was a measurement of Windows 11's application redesigns, not of
WinAppDriver. Every figure from this suite is conditional on the operating
system and the installed applications; state both or state neither.

The 70-test backlog, grouped: 20 `ActionsError_*` (pure payload validation, no Actions
implementation needed), 13 window size/position/maximize, 7 touch + mouse, 8 element
location/size, 4 `GetActiveElement`, 3 `CompareElements`, 4 session lifetime, 2 navigation,
2 orientation, rest assorted stale-element paths. **Roughly 40 of 70 are missing JWP routes plus
request validation — mechanical work.**

3 tests (`EdgeBase`: TouchClick ×2, TouchFlick ×1) target legacy EdgeHTML, which does not exist on
Win11 at any version. They can never pass.

---

## 5. UIA and COM implementation knowledge

### IntPtr everywhere for COM out-parameters

If `hwnd` needs `IntPtr`, so does every COM out-param returning an interface pointer. Declaring
`out IUIAutomationElement` caused `InvalidCastException: Specified cast is not valid` at runtime —
the marshaler returned a pointer and failed to wrap it in a custom interface definition.

- All `out` params returning COM interface pointers → `out IntPtr`
- Condition params → `IntPtr` (opaque handles; never call methods on them)
- Marshal with `Marshal.GetObjectForIUnknown(ptr)` + cast at the boundary
- The managed wrapper owns marshaling; callers never see raw pointers unless they ask

### Key interfaces and IDs

| Interface | GUID |
|---|---|
| `IUIAutomation` | `14314595-B0AD-4A2C-B385-AC53C31A1D25` |
| `IUIAutomationElement` | `D827F2C0-3771-4AD9-872E-F0246972138F` |
| `IUIAutomationCondition` | `352FFBA8-0973-437C-A6E3-20FA2465F1AC` |
| `IUIAutomationInvokePattern` | `FB377FBE-8EA6-46D5-9C73-6499642D3059` |
| `IUIAutomationValuePattern` | `A9468346-2255-4FF4-A07C-75353AE7E3E5` |
| `IUIAutomationSelectionItemPattern` | `A8EFA66A-0FDA-421A-9194-38021F3578EA` |
| `IUIAutomationExpandCollapsePattern` | `619B0F0D-0936-427C-8936-843FEE4CCFB0` |

Property ids: ControlType 30003, Name 30005, AutomationId 30011, ClassName 30012,
HasKeyboardFocus 30026, IsEnabled 30010, NativeWindowHandle 30020, BoundingRectangle 30001,
ProcessId 30002, RuntimeId 30007.

Pattern ids: Invoke 10000, Value 10002, ExpandCollapse 10005, SelectionItem 10010,
**Toggle 10015**, **ScrollItem 10017**.

### Pattern lifetime

Pattern objects are COM interfaces and must be released. Do not cache them — the element they came
from may go stale. Get, use, release.

### Element identity

RuntimeId string is the element id. WinAppDriver's docs show it dot-separated
(`42.333896.3.1`); the old implementation used commas. This does **not** affect conformance —
`FindElement_ByRuntimeId` round-trips the driver's own returned id — but it does affect anyone
hand-writing ids copied from `inspect.exe`. Pick one and document it.

Treat **three** states as distinct, not two:

1. alive
2. stale (`UIA_E_ELEMENTNOTAVAILABLE`)
3. **identity not yet resolvable** — an element caught mid-teardown. WinAppDriver returns something
   half-formed here and the client throws `InvalidOperationException`, which callers catching
   `NoSuchElementException` do not catch. Map it to `stale element reference` so callers have one
   thing to handle.

### COM exception mapping

| HRESULT | Constant | WebDriver error |
|---|---|---|
| `0x80040200` | `UIA_E_ELEMENTNOTAVAILABLE` | `stale element reference` (404) |
| `0x80040201` | `UIA_E_ELEMENTNOTENABLED` | `element not visible` |

ASP.NET detail worth keeping: the exception handler needs
`ExceptionHandlerOptions.AllowStatusCode404Response = true`. Without it ASP.NET treats a 404 from
an exception handler as handler failure, throws, and then fails again writing to a closed response
stream → `ObjectDisposedException: Cannot access a closed Stream`.

### Click semantics — the single most valuable behavioural difference

**Read `docs/CLICK-SEMANTICS.md` first.** It is the design requirement, written
from the primary source, and it contains two things this summary originally
missed: the ladder must walk **ancestors**, and the application under test had to
add transparent `Button` overlays and fall back to FlaUI to work around the
driver.

The mis-attribution worth knowing: an earlier memory entry recorded
"#1079 reproduced constantly" during a CollectionView rebind. That is wrong twice
over. The observed problem was **clicks not reaching handlers**, not finds
returning empty, and the cause was an `AutomationId` on a pattern-less `Border`
inside the item container whose parent held `SelectionItemPattern`. Nothing to do
with #1079.


WinAppDriver's Element Click is **synthesized mouse input at the bounding-box centre in SCREEN
coordinates**. It does not scroll first and does not check the point lies inside the target window.

Field evidence from PokemonBattleJournal (MAUI/WinUI 3, 83 Windows Appium tests driven daily for
weeks): at a 754×512 window on a 1920×1080 desktop, elements ~545px below the fold produced clicks
at y≈1057 — the taskbar. Runs launched Visual Studio and the Epic Games store mid-suite. On CI,
where the app fills the desktop, the identical click lands on empty desktop and **returns
success** — a silent no-op reported as a successful click, while every find-only test keeps
passing. Replacing coordinate clicks with a pattern ladder took that suite to 83/83 at the same
window size.

Recommended ladder:

| Step | Pattern | ID | Covers |
|---|---|---|---|
| −1 | **guarded mouse click, if a menu is open** | — | a modal menu loop holds input; nothing below can end one |
| 0 | ScrollItem, if supported | 10017 | bring into view first |
| 1 | Invoke | 10000 | buttons, links |
| 2 | **Toggle** | 10015 | checkboxes, switches |
| 3 | SelectionItem | 10010 | list/tab items |
| 4 | ExpandCollapse | 10005 | combos, menus |
| 5 | **guarded** mouse click | — | gesture-recognizer elements |
| 6 | throw `ElementNotInteractable` | — | never a silent success |

`CheckBox` and `Switch` expose Toggle and **not** Invoke — the old ladder omitted Toggle, so they
fell through to `SetFocus()` and silently did nothing. `SetFocus`-then-return must never be the
fallback: it reports success while doing nothing, the same defect class as the coordinate click.

#### Step −1: a click has side effects the pattern does not, and a menu is the case

**Measured 2026-08-30, and it is the one place the ladder's advantage costs
compatibility.** Everything from step 0 down reaches the provider directly and
sends no input at all. That is the point — but it also means none of them can
**end a modal menu loop**, which only real input does.

The compatibility suite depends on exactly that. `MouseClick` right-clicks
Calculator's title bar to raise the system menu and then dismisses it, with the
suite's own comment on the line:

```csharp
clearButton.Click(); // Dismiss the context menu
```

Served as an `Invoke`, the menu survives, and the two tests that run next both
act on the title bar underneath it — `MouseDoubleClick` and `MouseDownMoveUp`,
which were counted as two separate defects for weeks.

```
dismissed by             menu open   then maximized     (2 rounds each)
element click (Invoke)   YES         no
moveto + click (REAL)    no          YES
```

Three properties of the rung matter more than the rung:

- **It does not dismiss and then invoke.** A real click outside a menu is
  swallowed by the dismissal — Windows' behaviour, which binds the reference too.
- **It falls through on a mouse refusal** rather than returning it, so it can only
  add behaviour. The suite's own alarm-delete helper clicks `Delete` *inside* a
  popup that owns its window handle, where the point-ownership guard says no.
- **It is the Win32 modal loop only** (`GUI_INMENUMODE`). A WPF `ContextMenu` or
  a WinUI flyout runs no such loop and neither needs it nor trips it.

A mouse fallback **is** required — a MAUI `Border`, `Grid` or `Image` with a `TapGestureRecognizer`
exposes no pattern at all. Guard it: scroll into view, **re-read the bounding rect after scrolling**
(the pre-scroll rect is stale), compare the point against the **window** rect rather than the
desktop, and if it still falls outside, **throw naming both rects** — never dispatch.

Known divergences from a real mouse click, to document rather than discover:

- **Occlusion** — `Invoke()` succeeds on a covered element; W3C specifies
  `element click intercepted`.
- **Focus** — a mouse click focuses as a side effect; `Invoke()` does not. Breaks apps relying on
  validation-on-blur or commit-on-focus-loss.
- **Hover** — no pointer movement, so nothing gated on `PointerOver` fires.

Consider exposing both deliberately (`windows: invoke` vs `windows: mouseClick`) with W3C Element
Click defaulting to the safe ladder.

### Other behaviours to decide once and write down

WinAppDriver's damage comes from these being conditional and undocumented:

- **Scroll on click** — it *does* scroll, but only for elements whose container exposes
  `ScrollItemPattern`. A test that read Location, clicked, then read again saw a **132px**
  difference. Documented as intended ("we implicitly scroll elements within the view when they are
  selected") but conditional, so callers cannot tell whether a coordinate is stale. Pick a uniform
  policy and state it.
- **`GetText`** — on Windows an Editor's text is the value alone; on Android the same element
  returns `content-desc + ", " + value`. Cross-platform assertions had to anchor to the *end* of
  the string. Pick one and say so.
- **Popups** — a popup that is its own top-level window is invisible to a search rooted at the app
  window. MAUI `Picker` dropdowns are separate top-level windows. Needs an explicit stated policy.
  (Note: Win11 Alarms' Add-Alarm dialog is **not** one of these — it is a ContentDialog in the same
  window.)
- **Implicit wait cost** — a present element resolves in ~215ms; an **absent** one costs the entire
  implicit wait (5s ambient → ~6.8s per miss). A fixture doing a dozen optional-element checks
  spent a minute waiting for things it expected not to find. Support a per-request timeout
  override, or absence is a silent performance trap.
- **Window geometry** — Windows cascades each new window ~26px down-right, so consecutive local
  runs start at different origins. Anything reproducing a geometry-sensitive bug must pin position
  as well as size. The single highest-value diagnostic added in that whole MAUI investigation was
  **logging the window rect at session start** — consider putting window and element rects in
  session and error payloads by default, so this class of bug becomes self-diagnosing.

---

## 6. Client quirks (Selenium 3.8 / old Appium .NET)

The reference client is `Microsoft.WinAppDriver.Appium.WebDriver.1.0.1-Preview`, built on Selenium
WebDriver 3.8. It negotiates protocol at session creation and is inconsistent about it.

- **Sends POST to endpoints the W3C spec defines as GET.** Every element property route must accept
  **both** verbs. If it does not, ASP.NET returns 405, the body is a JSON *string*, the client
  deserializes `Response.Value` as `System.String`, and `RemoteWebElement.get_Location` does
  `(Dictionary<string, object>)response.Value` → `InvalidCastException`.
- **`FindElementByClassName` sends `"css selector"`** with a `.`-prefixed value (`".Edit"`) —
  appium/dotnet-client#265. WinAppDriver treated `css selector` + `.` prefix as a class-name
  search. Match that; reject other `css selector` values.
- **Newtonsoft deserializes `{"value":{...}}` to `JObject`, not `Dictionary`.** Methods the Appium
  driver overrides (`Location`, `Size`) work; methods it does not
  (`LocationOnScreenOnceScrolledIntoView`) do `response.Value as Dictionary<string,object>`, get
  `null`, and throw `NullReferenceException`. That is a client bug, not ours — but it caps
  `location_in_view` conformance.
- **`session.Keyboard.SendKeys()` crashes** with `NullReferenceException` inside
  `AppiumCommandExecutor.Execute`. Client-side; `/keys` over raw HTTP is fine. Note that the old
  server's `/keys` never worked either, so this category has never actually been measured.

---

## 7. Windows 11 app drift (for writing our own tests)

Our own suite should assert **behaviour**, not these ids. They are facts about one app version and
are recorded so nobody re-derives them.

**Alarms & Clock 11.2606.11.0** — the add/edit alarm page is now a WinUI `ContentDialog` in the
**same** window (tree grows 42 → 76 nodes when it opens):

| Suite expects | Win11 actual |
|---|---|
| `CancelButton` | **`CloseButton`** (Name still `Cancel`) |
| `Back` | *gone* |
| `AlarmSaveButton` | **`PrimaryButton`** (Name still `Save`) |
| `AlarmNameTextBox` | *no AutomationId*; `Edit` named `Alarm name` |
| `EditAlarmHeader` = `"NEW ALARM"` | `"Add new alarm"` |

Still correct: `AlarmButton`, `StopwatchButton`, `ClockButton`, `TimerButton`, `FocusButton`,
`AddAlarmButton`, `EditAlarmsButton`, `AppName`, `AlarmToggleSwitch`. Nav items are still
`ControlType.ListItem`, though `ClassName` is now
`Microsoft.UI.Xaml.Controls.NavigationViewItem`. Window title is **`Clock`**, not `Alarms & Clock`.

**Calculator 11.2606.0.0** — mode header reads `"Standard Calculator mode"`, not `"Standard"`.

**Notepad 11.2606.15.0** — edit surface `ClassName` is **`RichEditD2DPT`**, not `Edit`.
`C:\Windows\System32\notepad.exe` still exists but is a stub that redirects to the packaged app.

**Legacy Edge** — `Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge` does not exist on Win11.
Unrecoverable.

That one Alarms rename cascade costs the suite ~167 tests: `DismissAddAlarmPage()` hunts for
`CancelButton` then `Back`, finds neither, throws, and every downstream Alarms test inherits an app
parked on the dialog.

---

## 7b. Learned while building it (2026-08-08, implementation phase)

Everything above was learned by researching. This section is what only appeared
once code ran. See also `docs/LIMITATIONS.md`, which tracks the live list.

### UIA rejects RuntimeId as a property condition

`CreatePropertyCondition(UIA_RuntimeIdPropertyId, int[])` fails with
`E_INVALIDARG`. Not documented anywhere obvious; found by trying it and watching
two integration tests fail. A search by element id therefore has to enumerate
descendants and compare, which costs a full tree walk per by-id find.

That cost is the price of holding no cache, and it is worth paying: the
alternative is keeping elements alive between calls, which is the exact design
that produces #857 and #1079.

### `ActivateApplication` reports an unknown package as `E_INVALIDARG`

Which `Marshal.ThrowExceptionForHR` maps to **`ArgumentException`**, not
`COMException` — so a handler written for COM exceptions misses it entirely and
the exception escapes the launcher. Read HRESULTs directly rather than throwing
and guessing which type each maps to.

**This also finally explains a string the old implementation carried for years:**
`"Value does not fall within the expected range"` is `E_INVALIDARG`'s stock
message. It was surfaced to clients without anyone ever identifying what produced
it.

### Windows 11 `notepad.exe` defeats both obvious window searches

The path is a stub that starts the packaged Notepad and exits, so the launched
process owns no window. And Notepad is WinUI 3, so the window is not an
`ApplicationFrameWindow` — the packaged-app fallback misses it too. Found by a
ten-second timeout in an integration test on its first run.

The search now widens in stages: launched process → any descendant → any
top-level window that did not exist before the launch. Finding descendants needs
the parent process id, which neither WMI nor `Process.GetProcesses` exposes, so
it reads Toolhelp32 directly.

### Capability rules that contradict the obvious reading

All measured against WinAppDriver, all four the opposite of what a spec reading
gives:

- Supplying **both** `app` and `appTopLevelWindow` is **rejected**, with the same
  message as supplying neither. Exclusive, not preferential.
- An empty `app` has its own message: `Capability: app cannot be empty`.
- `capabilities.alwaysMatch` is **not understood at all**.
- Unrecognised capabilities are **dropped from the echo** — `deviceName` vanished,
  `platformName` survived. The list in `AuthoringTestScripts.md` is an allowlist,
  not documentation.

### Tooling constraints worth knowing before they cost an hour

- `LibraryImport` cannot marshal `[Out] char[]` without disabling runtime
  marshalling assembly-wide (SYSLIB1051), nor a struct with a fixed-size inline
  string. Both cases stay on `DllImport`.
- `LibraryImport` requires `AllowUnsafeBlocks`; the generated stubs are unsafe.
- A COM coclass cannot be cast to its interface directly — go through `object` so
  the runtime issues a `QueryInterface`.
- A COM interface is a vtable in declaration order. Unused methods must still be
  declared or later methods bind to the wrong slot.

### A false negative in my own verification loop

Mutation testing by hand has a failure mode that looks exactly like success.
Several mutations **broke the build** rather than failing a test — orphaning a
private method, tripping a style analyzer — and a grep filtered to test results
then printed nothing at all, which reads identically to "no test caught it".

Two rules came out of it, both now habit:

1. A mutation must **assert that it applied**. A scripted find-and-replace that
   silently matches nothing produced a "fix" that existed only in a commit
   message — the gate it claimed to add was never wired, and only running the
   real executable caught it.
2. A mutation must be **analyzer-clean**, or it is testing the analyzers rather
   than the tests.

### Test seams pay off immediately, and visibly

`IApplicationLauncher` and `IWindowLocator` exist so session creation is testable
without a desktop. Every branch — packaged, classic, desktop, attach, and each
failure — is covered by protocol tests that need no UI session at all. The
implementation being replaced launched processes inline in the route handler,
which is precisely why none of its session behaviour was ever tested.

The converse also held: the two defects above were found by the *integration*
tests on their first run, because those are the only tests that exercise anything
below the seam.

## 8. Mistakes not to repeat

### In the code (all measured in the old implementation)

- **`AddHostedService<T>` + `GetRequiredService<T>()`** — `AddHostedService` registers only
  `IHostedService`, so the concrete resolve throws at shutdown and cleanup never runs. This is why
  a manual "kill orphaned Calculator windows" snippet lived in CLAUDE.md.
- **`Close(pid)` killed every process sharing the name** — the first branch, and it returned, so
  the documented 5-step graceful cascade behind it was unreachable dead code. Ending a Notepad
  session killed every Notepad the user had open.
- **`ClickAt(id, x, y)` ignored x and y** — body was one line, `Click(elementId)`.
- **`/keys` injected nothing.** `InputUnion` declared only `KEYBDINPUT`, so `INPUT` marshalled to
  **32 bytes** where Win32 requires **40** on x64 (28 on x86). `SendInput` validates `cbSize` and
  fails; the return value was discarded, so it failed silently forever. The union must be sized for
  `MOUSEINPUT`.
- **`CharToVirtualKey` was wrong almost everywhere** — lowercase letters mapped to `0x61`, which is
  `VK_NUMPAD1`; uppercase mapped without shift; the Selenium private-use-area table was off by one
  from `\uE00B` and assigned F1–F12 to the numpad range, leaving the real F-key range unmapped and
  silently dropped. Use `KEYEVENTF_UNICODE` with `wVk=0, wScan=ch` for printable text and a small
  explicit table only for `\uE0xx` control keys.
- **`FindAumidForExe` shelled out to PowerShell** with the `app` capability interpolated into
  `-Command`. Windows filenames may contain single quotes, so it is a real command-injection
  vector. Use the WinRT `PackageManager` API.
- **`/window/handles` returned child windows** via `EnumChildWindows` while `POST /window` rejected
  anything non-top-level — the driver advertised handles it refused to accept.
- **34 silent `catch { }`**, one error string repeated 13 times, `DualGetPost` defined twice,
  session-lookup-or-throw inlined 23 times next to an existing helper, screenshot capture inlined in
  two route handlers, `IScreenshotCapture` declared with zero implementations and zero usages.
- **`IElementInteractor` returned JSON strings** that routes then `JsonDocument.Parse`d and
  re-serialized. The automation layer knew the wire format. This is the single biggest obstacle to
  reusing the automation layer, and the reason a rewrite beat a refactor.
- **`ElementStore` overwrote entries without disposing**, leaking an RCW per re-find, and was never
  session-scoped.
- **`RawAutomationElement` used `_element!` on 25+ members** while also exposing a properly-guarded
  `Element` property. After `Dispose()`, `IsAlive()` threw `NullReferenceException`, which its
  `catch (COMException)` did not catch.

### In the reasoning

The Alarms failure took **four wrong explanations** before the right one, and every one was
plausible from the logs:

1. "`AlarmButton` no longer exists on Win11" — false, it is in the tree.
2. "Our missing implicit wait causes it" — false, WinAppDriver has one and fails identically.
   (The implicit wait *is* genuinely missing and *is* a real defect — just an unrelated one.)
3. "A static session field, or a surviving single-instance app process, carries poisoned state" —
   false, the app terminates on `Quit()` and a fresh session recovers fully.
4. "WinAppDriver's `Invoke` on `AddAlarmButton` is a silent no-op" — false, it opens the dialog
   reliably, and closes it correctly when given `CloseButton`.

The answer only appeared from driving the real app step by step and checking state after every
single action. **Log-reading generates hypotheses; only manipulation settles them.**

The meta-lesson that produced all four: for two weeks the only measurement available was a
third-party suite whose failures were ambiguous between our bug, app drift, and client bug. A
number with no attribution is not a measurement. Which leads to:

### In the process

**Do not use a conformance suite as a TDD oracle.** They are different tools:

- WinAppDriver's `WebDriverAPI` suite is an **acceptance gate** — keep it pristine, run it
  occasionally, never modify it. An edited conformance suite does not measure conformance.
- TDD needs an **inner loop you own**: tests for behaviour that does not exist yet, unambiguous
  failures, seconds of feedback, isolation so one broken fixture cannot hide 167 results.

Conflating them cost three weeks. The plan going forward is two suites: ours carries the *logic* of
the WinAppDriver tests — the protocol contracts they verify — asserted against targets we control
and located robustly; theirs stays untouched as an external check.

`WinAppDriver/ApplicationUnderTests/` ships **AppUIBasics**, **Xaml-Controls-Gallery** and **Input**
as buildable source. Frozen, versioned, ours. No Store app can drift underneath them. That is the
right integration target — not inbox apps.

**The root cause of two weeks of drift was structural, not intellectual:** no seams → no tests →
nothing holding the design in place between sessions. A rewrite done the same way lands in the same
place. Test-first is the whole condition on which the rewrite is worth doing.

# Plan — element identity, properties, and click

Written before the code, per the working agreement. Everything in §1 was measured
against real WinAppDriver 1.2.2009.02003 on Windows 10.0.26200 on 2026-08-08 and
merged into `tests/WindowsDriverCore.Tests.Protocol/Recordings/winappdriver-responses.json`
(134 records). Anything not measured is marked as such and is not allowed to
become an assertion until it is.

---

## 1. The measured contract

### 1a. `tag name` matches the ControlType programmatic name, case-sensitively

The condition that separates the hypotheses: a control type whose programmatic
name and localized name differ by more than case.

| `using: tag name`, `value:` | Result |
|---|---|
| `Button` | 200, element |
| `button` | 404 no such element |
| `ControlType.Button` | 404 |
| `ListItem` | 200, element |
| `list item` | 404 |
| `ListViewItem` | 404 |
| `Text` | 200, element |
| `text` | 404 |

`ListItem` matching while `list item` does not rules out LocalizedControlType,
and `Button` matching while `button` does not rules out a case-insensitive match
on either. It is `UIA_ControlTypePropertyId` compared against the enum's
programmatic name, without the `ControlType.` prefix.

**`Locator.cs` is therefore wrong today**, and its comment argues confidently for
the wrong answer:

> NOT ControlType. WinAppDriver matches LocalizedControlType, a localized display
> string — "Button", "Edit".

The example given in that comment is the exact input where the two hypotheses
predict the same observation, which is why it survived. `ListItem` is the input
where they differ. This is the "wrong condition" failure from the testing
standard, committed in a comment rather than a test.

`LocatorKind.LocalizedControlType` becomes `LocatorKind.ControlType`, carrying an
`int` property value rather than a string.

### 1b. `/name` is the tag name, not the Name property

`GET /element/{id}/name` returns `ControlType.Button`, `ControlType.Text`,
`ControlType.Edit` — **with** the prefix, while the locator takes it **without**.
Confirmed independently by WinAppDriver's own `ElementName.GetElementTagName`,
which asserts `header.TagName == "ControlType.Text"`.

The Name *property* is reachable only through `/attribute/Name`.

### 1c. `/text` is ValuePattern first, Name second

The previous run could not answer this: every Calculator element had a Name and
no ValuePattern, so "value if available else name" and "always name" predicted
the same observation. Settings' search box is the input where they differ — it
has both, and its value starts empty.

| Subject | Name | Value | `/text` |
|---|---|---|---|
| Settings search box (Edit) | `Search box, Find a setting` | *(empty)* | `""` |
| …after typing `printers` | `Search box, Find a setting` | `printers` | `printers` |
| …after `/clear` | unchanged | *(empty)* | `""` |
| Settings minimize button (control) | `Minimize Settings` | no ValuePattern | `Minimize Settings` |

An empty ValuePattern value beats a non-empty Name. That is decisive: the rule is
pattern availability, not string emptiness.

The control matters as much as the subject — the button in the same window at the
same moment still returns its Name, so the observation is about the element, not
about the window or the session.

**Third case, not measured here:** WinAppDriver's `ElementText.GetElementText`
asserts `MinuteLoopingSelector.Text == "00"`, a List returning its selected
item's value. So the ladder is ValuePattern, then Selection, then Name. The
Selection rung is taken from Microsoft's test rather than from a measurement of
mine, and is labelled as such in the code.

**Divergence worth noting:** `/text` returns `""` for an empty value, but
`/attribute/Value.Value` returns `null` for the same element in the same state.
Two endpoints, two answers, same underlying property.

### 1d. `/location` is window-relative; every other geometry is screen-relative

One element, read back to back:

| Reading | Value |
|---|---|
| `/location` | `{x: 203, y: 419}` |
| `/size` | `{width: 97, height: 35}` |
| `/attribute/BoundingRectangle` | `Left:257 Top:616 Width:97 Height:35` |
| `/attribute/ClickablePoint` | `305,633` |
| `/window/current/position` | `{x: 54, y: 197}` |

`257 − 203 = 54` and `616 − 419 = 197`, exactly the window origin. `/size`
matches the bounding rect unchanged, and `ClickablePoint` is the screen-space
centre (`257 + 97/2 = 305.5`, `616 + 35/2 = 633.5`).

So `/location` and `/location_in_view` report **client coordinates relative to
the session window**, while `BoundingRectangle`, `ClickablePoint` and any mouse
input work in **screen coordinates**. The same API mixes two coordinate spaces.

This is load-bearing for the click ladder: a mouse fallback that fed `/location`
back to `SendInput` would be off by the window origin, which is invisible on a
window at the top-left of the primary display and wrong everywhere else.

An earlier reading of the same disagreement guessed DPI scaling. It was not — the
ratios did not match, and asking the window where it was settled it in one call.

### 1e. `/attribute/{name}` is an open surface, not a fixed list

Anything UIA exposes, by string, on 28 names measured:

| Shape | Examples | Rendering |
|---|---|---|
| String properties | `Name`, `AutomationId`, `ClassName`, `FrameworkId`, `LegacyName` | verbatim |
| Booleans | `IsEnabled`, `IsOffscreen`, `IsKeyboardFocusable`, `IsContentElement` | `"True"` / `"False"` |
| Integers | `ProcessId` | `"3155156"` |
| Runtime id | `RuntimeId` | `"42.61409678.4.100"` — dots |
| Point | `ClickablePoint` | `"305,633"` |
| Rect | `BoundingRectangle` | `"Left:257 Top:616 Width:97 Height:35"` |
| Control type | `ControlType` / `LocalizedControlType` | `"ControlType.Button"` / `"button"` |
| Enum | `Orientation` | `"None"` |
| Pattern availability | `Is{Pattern}PatternAvailable` | `"True"` / `"False"` |
| Pattern-qualified | `SelectionItem.IsSelected`, `Value.Value`, `Value.IsReadOnly` | as the property |
| Unset property | `HelpText`, `AcceleratorKey`, `AccessKey` | `null` |
| Unknown name | `InvalidAttributeName` | `null`, HTTP 200 |
| Empty name | *(route is `/attribute/`)* | 400, status 100, `"Attribute command takes exactly one argument namely the attribute name"` |

An unknown attribute is **not** an error. A caller cannot distinguish "no such
property" from "property is unset", and neither can this driver without inventing
a divergence.

`LocalizedControlType` remains reachable — as an attribute, which is where it
belongs. Removing it from the locator does not lose it.

### 1f. Boolean and geometry endpoints

`/enabled`, `/displayed`, `/selected` return real JSON booleans, not the
capitalised strings the attribute route uses. `/rect` is **501** — W3C only, and
WinAppDriver does not implement it. `/clear` on a Button with no ValuePattern
returns **200**, not an error.

`/displayed` is `!IsOffscreen`: WinAppDriver's `ElementDisplayed` test has two
sibling looping-selector items where the scrolled-out one reports `false` while
still being findable, and scrolling swaps which is which.

---

## 2. What has to be built, and in what order

The order is not the order the items were listed in. It is forced by two
dependencies:

**Every interaction endpoint needs to turn an element id back into an
`IUIAutomationElement`, and nothing does that yet.** `IElementFinder` returns
strings. That resolution step is shared by all nine endpoints, so it is one piece
of work, not nine.

**The click experiment needs `/text` and `/selected` as its instrument.**
`CLICK-SEMANTICS.md` specifies the measurement as "did the handler run — observed
through app state, not through the driver's own report of success". Observing app
state means reading a property. Building the click first would leave it verified
by the driver's own success report, which is precisely the defect being fixed.

So: identity, then properties, then click.

### Step A — `feat/rewrite/element-identity`

**A1. Fix the `tag name` locator** (§1a). `LocalizedControlType` becomes
`ControlType`; the locator carries the UIA control-type id. A regression test with
`ListItem` and `list item` as the two conditions, because `Button` alone cannot
tell the implementations apart.

**A2. `IElementResolver` — id back to element.**

RuntimeId cannot be used in a property condition (`E_INVALIDARG`, recorded in
`LIMITATIONS.md`), so resolution is a descendant walk comparing formatted ids.
Same cost as a find, and it is paid once per interaction call.

**A3. Stale versus unknown, without caching elements.**

WinAppDriver distinguishes them: an element it issued that has since died is
status 10 (stale element reference); an id it never issued is status 7 (no such
element). And it is destructive — the first touch reports 10, the next reports 7,
because the first evicts the entry.

We hold no COM references, deliberately, so the obvious mechanism is unavailable.
The cheap equivalent is to **remember the ids, not the elements**: a
`HashSet<string>` per session of ids handed out. Resolution then answers three
ways —

- found in the tree → the element
- not found, id was issued → status 10, and remove it from the set
- not found, id never issued → status 7

which reproduces the destructive behaviour exactly, at the cost of one string per
element ever returned, and holds nothing that can go stale. A set of strings
cannot drift from the tree the way a set of element references can; that is the
whole reason this design is available to us and not to WinAppDriver.

Bounded growth is the open question. A session that finds a thousand elements
keeps a thousand short strings, which is nothing; a session that runs a find in a
loop for an hour is a different matter. Not solved in this step, and recorded in
`LIMITATIONS.md` rather than pre-optimised.

**A4. `RequiresElement()`** — the sibling of the existing `RequiresSession()`
filter. Resolves `{elementId}`, short-circuits with 7 or 10, and stashes the
element for the handler. One place where every endpoint's stale check lives,
rather than nine copies of it.

### Step B — `feat/rewrite/element-properties`

Nine endpoints over the resolver, all read-only:

| Route | Source | Note |
|---|---|---|
| `/name` | `ControlType` | `"ControlType.Button"`, prefixed (§1b) |
| `/text` | Value → Selection → Name (§1c) | |
| `/attribute/{name}` | open UIA surface (§1e) | `null` for unknown; 400/100 for empty |
| `/enabled` | `IsEnabled` | real boolean |
| `/displayed` | `!IsOffscreen` | |
| `/selected` | `SelectionItem.IsSelected`, `false` when absent | not an error |
| `/location` | bounding rect **minus window origin** (§1d) | |
| `/location_in_view` | same as `/location` | |
| `/size` | bounding rect, screen scale | |

`/rect` stays 501 to match.

The attribute route is where the DRY pressure is: twelve renderings in §1e, each
of which is "read a UIA property, format it". A rendering table keyed by property
id, with the pattern-qualified names as a second small table, rather than a switch
that grows a case per attribute anyone asks for.

**Verification.** Each of the nine gets a protocol test asserting the exact
recorded body, and each rendering rule gets an inverse-edit check — break the
rule on purpose, watch the right test go red, put it back. `/text` in particular:
the Settings-search-box condition is the only one that distinguishes the rules, so
the test uses it and not a Calculator button.

### Step C — `feat/rewrite/element-click`

The ladder from `CLICK-SEMANTICS.md`, unchanged: ScrollItem, Invoke, Toggle,
SelectionItem, ExpandCollapse-after-Focus, Focus for Edit and Document, the same
ladder on up to three ancestors, guarded mouse, then throw.

Two things §1 changes about it:

- The guard compares against the **window** rect in screen coordinates, and the
  element rect must come from `BoundingRectangle` (screen), never from
  `/location` (window-relative). Mixing them would produce a guard that passes
  while the click lands in the wrong place — a wrong instrument, and a
  particularly nasty one because it fails only on non-top-left windows.
- The bounding rect is re-read **after** scrolling. The pre-scroll rect is stale
  by construction.

`/clear` and `POST /value` come with this step: both are ValuePattern writes, and
`/clear` on a pattern-less element returns 200 (§1f) rather than erroring.

**The experiment**, per `CLICK-SEMANTICS.md`: a 754×512 window on a 1920×1080
desktop, an element below the fold, measured through `/text` on the app rather
than through the click's own status; control is the same element through real
WinAppDriver, which is observed to miss. Effect size is why the window size is
specified — a maximised window does not reproduce it, and a previous
investigation concluded the theory was dead on exactly that mistake.

`ElementNotVisible` on clicking a non-displayed element is asserted by
WinAppDriver's own `ClickElementError_ElementNotVisible`, and is **not measured by
me** — the Win11 Alarms UI no longer reaches that state through the documented
path. It goes in as a rule with that provenance marked, not as a recorded fact.

### Then: benchmarks

With B and C landed there is a matched operation to benchmark — find, read a
property, click — against FlaUI in-process for the floor and WinAppDriver over
HTTP for the baseline. That is what "something to bench" meant, and neither
comparison is meaningful with find alone, because find is the one thing all three
already do.

---

## 3. Things this plan does not settle

- **`/text` on a Selection element** is Microsoft's assertion, not my
  measurement. First Win11 app found with a working looping selector settles it.
- **`ElementNotVisible` on click** — same provenance, same resolution.
- **Issued-id set growth** is unbounded within a session.
- **Resolution cost** is a full descendant walk per interaction call. Expected to
  dominate every property endpoint, and the reason `/text` will benchmark worse
  than FlaUI's, which holds the element. Measure before fixing; the last two
  performance theories on this project were both wrong.

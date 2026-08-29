# The protocol audit

A standing sweep for parameters, shapes and commands this driver silently
ignores. Run it as a workflow; it is **not clean until three consecutive passes
find nothing**, and each pass must use a different lens.

---

## Why this exists

`/touch/flick` ignored the protocol's own `speed` parameter for the whole life
of the route. A caller asking for a slow flick and a caller asking for a fast
one got the identical gesture, and the anonymous form — which sends `xspeed`
and `yspeed` and no offsets at all — was read as two absent properties
defaulting to zero, a gesture that goes nowhere.

**Nothing caught it.** The build was clean, every test passed, and the
compatibility score was two tests off the reachable ceiling. It was found by
reading the spec while fixing something else.

The owner's read, which is the reason for the standing sweep rather than a
one-off fix:

> "this likely means there missing stuff like this all over the place despite
> the fact we are really close to passing all the tests we can"

That was right. The same pass immediately found two more.

---

## THE REFERENCE IS THE SPEC, NOT WINAPPDRIVER

This is the rule that shapes everything below, and it is the owner's:

> "we dont want to follow winappdriver anyway because its flakey, slow, and not
> up to date, but we do want to implement the winappdrivers api plus more"

So:

- **Audit against JSON Wire and W3C**, and against what a real client (Selenium
  3 and 4, Appium) actually sends. Those are the contract.
- **WinAppDriver's behaviour is evidence, not authority.** Where it is measurably
  wrong — an unguarded coordinate click, a lookup that never checks ancestors —
  match the API and fix the behaviour.
- **"WinAppDriver does not implement it either" is NOT a reason to skip a
  parameter.** The goal is its API *plus more*. A gap it shares is still a gap.
- **The compatibility suite cannot be the audit.** It is one Selenium 3.8 client
  exercising one application. Every W3C request shape, every parameter it never
  sends, and every command it never calls is invisible to it — which is exactly
  where these defects live. A green score says nothing about this file.

---

## The three lenses

Three passes, and they must be **different passes**, not the same sweep run
three times. A static check repeated is one check with more steps; the point is
that different angles surface different gaps, and a pass is only meaningful if
it could have found something the previous one could not.

### Lens 1 — by ROUTE

Walk every endpoint the driver maps. For each, list what the spec defines as
its request body and its response, and confirm the code reads and returns all of
it.

```bash
grep -rhn "MapPost(\|MapGet(\|MapDelete(" --include=*.cs src/WindowsDriverCore.Protocol/Routing/ \
  | grep -o '"/session[^"]*"' | sort -u
```

Ask of each: **what does the spec say this body contains, and does the code
mention every key?**

### Lens 2 — by PARAMETER

Invert it. Enumerate what the code reads, and look for spec parameters absent
from the list.

```bash
grep -rn "JsonPropertyName\|TryGetProperty" --include=*.cs src/WindowsDriverCore.Protocol/Routing/
```

This is the lens that found `speed`: the route existed, the body was read, and
one documented key was simply not in the record. By-route reading skims over
that — the route *looks* implemented.

### Lens 3 — by DIALECT

For every command where JSON Wire and W3C differ, confirm BOTH spellings are
accepted, and that a control proves the other one still works.

Known differences, and all three were defects here:

| command | JSON Wire | W3C |
|---|---|---|
| `element/{id}/value` | `{"value": ["h","i"]}` | `{"text": "hi"}` |
| `POST /window` | `{"name": "..."}` | `{"handle": "..."}` |
| `/timeouts` | `{"type","ms"}` | `{"implicit","pageLoad","script"}` |
| window size/position | `/window/current/size` | `/window/rect` |

**The response dialect was translated once, in a filter, and that made the
request shapes feel finished.** They were not. Each has to be read on its own.

---

## Three clean passes

A pass is **clean** when its lens finds nothing. The audit is clean when
**three consecutive passes are clean, one per lens, with no code change between
them.**

Any **new** finding resets the count. Fix it, then start the three again —
because a fix is itself a change that can introduce a gap, and the point of the
third pass is to see the code as it will actually ship.

**"New" is load-bearing, and it was missing here until pass 6.** An item already
in the ledger is a known gap, not a discovery; re-encountering one resets
nothing. Without that word the rule is unsatisfiable the moment anything needs
work beyond a route alias, since every later pass re-finds it. See *The
completion rule, corrected* further down — the correction is worth more than the
original wording.

Two rules that make the count mean something:

- **A pass that finds nothing because it looked at nothing is not clean.**
  Record what was covered, not just the verdict.
- **Do not run the same lens twice to reach three.** That is one pass with a
  bigger number attached.

---

## Findings ledger

Newest first. A finding stays here after it is fixed — the value is the pattern,
not the list of open items.

| date | lens | finding | status |
|---|---|---|---|
| 2026-08-29 | dialect | `POST /window` read only `name`; a W3C client switching windows got *"Missing Command Parameter: name"* | fixed, `W3CRequestShapeTests` |
| 2026-08-29 | dialect | `element/{id}/value` read only the JWP array; a Selenium 4 client typed NOTHING and got a 200 | fixed, `W3CRequestShapeTests` |
| 2026-08-29 | parameter | `/touch/flick` ignored `speed`; the anonymous `xspeed`/`yspeed` form was read as absent offsets and went nowhere | fixed |
| earlier | dialect | `/timeouts` and element `/rect` request shapes | fixed |
| 2026-08-29 | dialect | `GET /element/{id}/property/{name}` not served — W3C's spelling of `attribute` | fixed, aliased |
| 2026-08-29 | dialect | `GET /element/active` — W3C changed the VERB, we served POST only | fixed, aliased |
| 2026-08-29 | dialect | `POST /window/maximize` — W3C dropped the handle from the path | fixed, aliased |
| 2026-08-29 | dialect | `GET`/`POST /window/rect` not served — W3C replaced size+position and there is NO JWP fallback, so a Selenium 4 client could not read or set geometry at all | fixed |
| 2026-08-29 | dialect | `POST /window/minimize` not served | fixed, `IWindowLocator.Minimize` + `W3CRequestShapeTests` |
| 2026-08-29 | dialect | `POST /window/fullscreen` not served | **WILL NOT FIX as specified** — see below |
| 2026-08-29 | route (pass 4) | `GET /session/{id}/timeouts` not served — W3C reads them back and this driver could only be written to | fixed, `W3CRequestShapeTests` |
| 2026-08-29 | route (pass 4) | `POST /session/{id}/timeouts/implicit_wait` not served — the legacy JWP spelling, which Selenium 3.8 does not send | fixed, `W3CRequestShapeTests` |
| 2026-08-29 | route (pass 4) | `POST /session/{id}/orientation` not served — both dialects define it and we served only the GET | fixed, refuses PORTRAIT rather than accepting it |
| 2026-08-29 | parameter (pass 5) | **`key` input sources in `/actions` were SKIPPED and the request answered 200.** Every Selenium 4 keystroke goes through `ActionChains`, so that client could not type at all | fixed, `KeyActionRunner` + `ActionsKeySourceTests` |
| 2026-08-29 | parameter (pass 5) | `/touch/down`, `/touch/move`, `/touch/up` never set `InputPending` — the only input path that did not, so a read after a hand-built gesture outran the application | fixed, `DispatchedInputDrainsBeforeAReadTests` |
| 2026-08-29 | parameter (pass 5) | `requiredCapabilities` never read — the other half of the JSON Wire session body, so a client stating its app as required was told its capabilities were bad | fixed, `W3CRequestShapeTests` |
| 2026-08-29 | dialect (pass 6) | **`/window/{handle}/size`, `/position` and `/maximize` served only the literal `current`.** A client addressing a window by the handle the path exists to carry got unknown-command — six of WinAppDriver's own 59 documented endpoints | fixed, `AddressedWindow` + `W3CRequestShapeTests` |
| 2026-08-29 | dialect (pass 6) | The handle-less `/window/size` and `/window/position`, also in WinAppDriver's documented list, not served | fixed, same handler |
| 2026-08-29 | dialect (pass 6) | `POST /window/minimize` — carried OPEN since pass 3, now closed | fixed, `SW_SHOWMINNOACTIVE` so it does not steal the foreground |
| 2026-08-29 | dialect (pass 6) | `GET /session/{id}/location` — geolocation, in WinAppDriver's documented list and its own suite (`Location.cs`) | **OPEN** — see below |
| 2026-08-29 | dialect (pass 6) | `/execute`, `/execute_async`, alert handling, `/log`, `/log/types`, `/url`, `/refresh`, `/element/{id}/submit` | **OPEN, out of scope for this audit** — see below |
| 2026-08-29 | route | `DELETE /actions` (Release Actions) not served at all — a W3C client releasing input state got the unknown-command fallback | fixed, `W3CRequestShapeTests` |
| 2026-08-29 | route | `GET /window/handles` (W3C) not served — only `/window_handles` | fixed, aliased onto one handler |
| 2026-08-29 | route | `GET /window` (W3C current handle) not served — only `/window_handle`; POST and DELETE on that path existed, so the gap was one verb | fixed, aliased |

### Fullscreen is not maximize, and mapping it there would be a lie

W3C's Fullscreen Window is *"the window manager-specific full screen
operation"*. On Windows the window manager's operation **is maximize** — there
is no OS-level way to make an arbitrary window fullscreen. Real fullscreen is
app-specific: F11 in Explorer and browsers, custom elsewhere, absent in most.

The owner's read, and it is the right one:

> "maximize is full screen basically isnt it? only thing fuller screen that
> maximize is f11 or something, which wouldnt we just implement as the keypress,
> because not every app even uses full screen outside the stock one."

So there are three options and two of them are lies:

- **Map it to maximize.** Reports success for something else. This is the exact
  defect the driver exists to avoid — the same reason `ActionRoutes` refuses a
  valid payload it cannot perform rather than accepting it, and the same reason
  page-load and script timeouts answer 501 instead of being quietly stored.
- **Send F11 blindly.** A guess about the application, delivered to whatever
  holds the foreground, reported as success whether or not anything happened.
- **Refuse it.** Honest, and consistent with everything else here.

If fullscreen is ever wanted it belongs as a **vendor extension** the caller
opts into — `windows: fullscreen` sending F11 — which is the project's stated
rule: *"Where a capability has no expression in the protocol, add a vendor
extension rather than silently choosing for the caller."* The caller then knows
it is asking for a keystroke, not a window-manager operation.

`minimize` is different and is genuinely open: it is a real OS operation
(`ShowWindow(SW_MINIMIZE)`) and can be implemented honestly. It needs a new
`IWindowLocator` member, which is Platform work rather than a route alias.

### Passes

| date | lens | covered | result |
|---|---|---|---|
| 2026-08-29 | parameter | every request record and every `TryGetProperty` in Routing | **3 findings** — count reset |
| 2026-08-29 | route | every mapped endpoint against the W3C endpoint list | **3 findings** — count reset |
| 2026-08-29 | dialect | every command where JWP and W3C diverge | **6 findings** — count reset |
| 2026-08-29 | route (pass 4) | every mapped endpoint against the W3C endpoint list, re-run | **3 findings** — count reset |
| 2026-08-29 | parameter (pass 5) | every key the Routing layer reads, against each endpoint's defined body | **3 findings** — count reset |
| 2026-08-29 | dialect (pass 6) | all 59 endpoints in WinAppDriver's `SupportedAPIs.md`, probed against a running server, plus every W3C-only spelling | **4 fixed, 2 recorded open** — count reset |

**Still zero of three.** Six passes, every one productive — 3, 3, 6, 3, 3 and 6.
The count cannot start until a lens comes back empty.

### The completion rule, corrected — it was unsatisfiable as written

"Any finding resets the count" plus a backlog of items needing work beyond a
route alias means the count can **never** start: every pass re-finds the same
open items and resets on them. That is a rule that cannot be satisfied, which
makes it a rule nobody can act on.

**A pass is clean when it finds nothing NEW.** An item already in the table
above is a *known* gap, not a discovery, and re-encountering one does not reset
anything. What resets the count is a gap this document did not already name.

Two consequences worth stating, because the loophole is obvious:

- **An open item must be written down at the moment it is found**, with what it
  needs, or "already known" becomes a way to launder anything inconvenient.
- **Clean means "no new gaps", not "no gaps".** A clean audit with four open
  items is an honest state; a clean audit reached by not looking is not.

### Pass 6 changed the instrument, and that is why it found six

Passes 1–5 read source. Pass 6 **probed a running server**: all 59 endpoints in
WinAppDriver's own `SupportedAPIs.md`, plus every W3C-only spelling, each
classified by whether the response was the unknown-command fallback — with a
deliberate `/definitely-not-a-route` control proving the probe could detect an
absence at all.

That directly answers pass 4's instrument defect, where a literal-path grep
invented thirteen false positives. It also produced something no source read
would have: **six of the missing endpoints were in the reference's own
documented list**, not in the W3C spec. The audit had been looking outward at
W3C and had not checked the API this project exists to implement.

Three of the eleven "missing" were errors in WinAppDriver's own table — it
writes `GET` for `/element/:id/element`, which is a POST, and truncates
`/equals/:other`. Confirmed served under the correct verb and path rather than
assumed, because a doc typo and a real gap look identical from a probe.

**The probe belongs in the repo, not in a shell history.** Until it is,
`docs/PROTOCOL-AUDIT.md` is the only record that it was run and what it said.

**The by-parameter lens keeps out-earning the by-route one, and pass 5 says why
in one line.** `POST /actions` is *served*. It validates a dozen fields against
the spec's own messages, it performs pointer input, it has its own test fixture
and it answers 200. By route it is finished. By parameter it dropped every
keyboard sequence a Selenium 4 client can send — the runner's comment called key
sources "someone else's job" and no such job was ever written.

That is the shape to look for: not a missing endpoint, but **a served one whose
own comment defers work to somewhere that does not exist.**

### The instrument was wrong in pass 4, and it failed toward MORE work

The by-route sweep is a grep for literal paths, and it reported **16 endpoints
missing when only 3 were**. Nine of the thirteen false positives — `text`,
`name`, `enabled`, `displayed`, `selected`, `location`, `location_in_view`,
`size`, `rect` — are registered through a helper:

```csharp
MapRead(app, "text", …);      // ElementPropertyRoutes
```

The path exists only as a *parameter*, so no grep for `"/text"` can see it.
`MapAction` and `MapDrag` hide routes the same way.

**The direction of the error is the only reason this was survivable.** A grep
that cannot see a registered route reports it as missing, so the failure mode is
wasted time, not a missed gap — the audit stays sound and gets slower. The
mirror-image instrument, one that walked the registered endpoints and compared
them to the spec, would fail the other way and silently skip whatever it could
not enumerate.

Rule for later passes: **verify every "missing" by asking the running server,
not the source text.** A 404 from the driver is the measurement; a grep miss is
a hypothesis. And prefer instruments whose blind spot costs work rather than
coverage — see the same principle in `CLAUDE.md`'s note that a test whose own
mechanism can fail like the thing under test proves nothing either way.

---

## What a finding looks like when it is fixed

Not just the code. Each needs:

1. **A test the compatibility suite cannot provide.** These gaps are invisible to
   a Selenium 3 client, so a green suite is not evidence — see
   `W3CRequestShapeTests`, which exists purely for shapes the suite never sends.
2. **A control in the other dialect.** A change that teaches the route W3C and
   forgets JSON Wire trades the entire score for a dialect nothing in the suite
   speaks.
3. **A mutation check.** Disable the new spelling and watch the right test go
   red. Mutate a VALUE or a key string, never a use — removing a use orphans a
   constant and the BUILD fails, which looks exactly like an uncaught mutant.

---

## Open items, and why each is open

Recorded here at the moment they were found, per the completion rule above. Each
names what it needs, so "already known" cannot be used to launder work.

### `GET /session/{id}/location`

In WinAppDriver's documented list and in its own suite (`Location.cs`, which
asserts a latitude within ±90 and a longitude within ±180). Not served here at
all.

Needs `Windows.Devices.Geolocation.Geolocator`, which is a new Platform
capability and a new contract — and which raises an unauthorised error on a
machine without location consent, so most of the time the honest answer is a
refusal. Note that `GetLocation` is one of the nine environmental failures the
reference driver ALSO has on the guest, for exactly that reason.

**Fabricating a coordinate is not an option.** A plausible latitude is the same
defect as a 200 for an action that did not happen, and it is worse than a
refusal because the client cannot tell.

### `/execute` and `/execute_async`

WinAppDriver's vendor commands ride on `/execute` — `windows: startApp`,
`windows: click`, `windows: keys`, `windows: hover`, `windows: scroll` and the
rest. That is a real API surface of the thing this project reimplements, and it
is entirely absent here.

Large enough to be its own piece of work rather than an audit fix, and it wants
a decision first: which vendor commands to serve, and whether new ones belong
under `windows:` or a name of this driver's own. The project rule already points
at the answer — *"where a capability has no expression in the protocol, add a
vendor extension rather than silently choosing for the caller"* — but the
inventory is the work.

### Alert handling

`/alert_text`, `/accept_alert`, `/dismiss_alert` (JSON Wire) and `/alert/text`,
`/alert/accept`, `/alert/dismiss` (W3C). None served, in either dialect.

Windows modal dialogs are real and this driver already finds them — the Notepad
discard prompt is a WinUI `ContentDialog` in the app's own UIA subtree with no
HWND of its own, and `SecondaryButton` is matched by automation id today. So the
capability exists; what is missing is the routes and a definition of "the
current alert" on a desktop, where there is no single browser-modal concept.

### `/log` and `/log/types`

WinAppDriver serves both. This driver has a richer transcript than the reference
(`IInteractionLog`, the EventSource channel) and no way for a client to ask for
any of it.

Worth doing and cheap, but it needs the privacy rule applied deliberately:
locators are logged, `SetValue` and `SendKeys` arguments never are, and a log
endpoint is exactly where that boundary would be crossed by accident.

### `/url`, `/refresh`, `/element/{id}/submit`

Browser-shaped commands with no obvious desktop meaning. `back` and `forward`
ARE served, which makes the asymmetry deliberate-looking when it is not — they
exist because the suite tests them.

Needs a decision rather than an implementation: either serve them as the
keystrokes they would be (F5, Enter) or refuse them explicitly, the way
`/window/fullscreen` is refused. **Serving them as no-ops is the one option
ruled out.**

### Not gaps, recorded so they are not re-found

`/frame`, `/frame/parent`, `/element/{id}/shadow`, `/element/{id}/css/{name}`,
`/print` and `/window/new`. These are DOM and browser concepts with no UI
Automation counterpart — there is no document to switch into, no shadow root, no
stylesheet. Refusing them is correct, and the unknown-command fallback already
says so.

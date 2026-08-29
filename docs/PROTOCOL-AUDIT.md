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

Any finding resets the count. Fix it, then start the three again — because a
fix is itself a change that can introduce a gap, and the point of the third pass
is to see the code as it will actually ship.

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
| 2026-08-29 | dialect | `POST /window/minimize` not served | **OPEN** — needs a Platform capability, `IWindowLocator` has no minimize |
| 2026-08-29 | dialect | `POST /window/fullscreen` not served | **WILL NOT FIX as specified** — see below |
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

**Still zero of three.** All three lenses have now run once and every one found
something — 3, 3 and 6. The count cannot start until a lens comes back empty,
and on this evidence the next pass of any lens is more likely to find more than
to be the first clean one.

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

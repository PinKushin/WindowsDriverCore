# Serving both protocols

**Decision, 2026-08-11:** this driver serves JSON Wire Protocol **and** W3C
WebDriver. JWP stays the floor and the compatibility suite stays the scoreboard;
W3C is additive.

The reason is measured rather than aesthetic. **Selenium 4 dropped JWP**, so a
current Selenium cannot drive WinAppDriver at all, and that is the largest
technical cluster in its issue tracker — roughly 42 reactions across #1610,
#1839, #1997 and #1543. See `docs/WINAPPDRIVER-ISSUES.md`.

## Why this has to be designed now rather than retrofitted

A second dialect bolted on later means editing every route, and two code paths
that must be kept in step. That is exactly how WinAppDriver's own XPath singular
and plural paths drifted apart into issue #1079 — the same mistake this project
already refuses to repeat for finds.

So: **one set of route handlers, one translation at the boundary.** No route ever
learns which protocol it is answering.

## The seam

`JsonWireStatusFilter` already exists and already does the hard part. It is an
endpoint filter, so it sees the returned envelope **as an object, before
serialisation** — which is why it can read a JSON Wire status without buffering
the response body. That is the natural place for the dialect to be applied.

```
route handler  ->  IJsonWireEnvelope  ->  [filter]  ->  JWP or W3C on the wire
                   (protocol-neutral)      reshapes
```

The session records which dialect it was created with, and the filter reshapes
accordingly. Routes keep returning what they return today.

## What differs, concretely

### 1. Session creation — the one place the dialect is decided

| | JWP | W3C |
|---|---|---|
| request | `{"desiredCapabilities": {...}}` | `{"capabilities": {"alwaysMatch": {...}, "firstMatch": [...]}}` |
| response | `{"sessionId": "...", "status": 0, "value": {...}}` | `{"value": {"sessionId": "...", "capabilities": {...}}}` |

Which key the request used **is** the dialect. Nothing else needs to negotiate.

### 2. Capabilities must be vendor-prefixed

W3C rejects unknown top-level capabilities, which is issue #1543 verbatim:
`app` and `appArguments` are refused by Selenium 4. The W3C spelling is
`appium:app`, `appium:appArguments`. Accept both; emit the prefixed form back.

### 3. The envelope

JWP wraps everything with a numeric `status`. W3C has none: success is
`{"value": ...}` and failure is an HTTP 4xx/5xx carrying
`{"value": {"error": "no such element", "message": "...", "stacktrace": ""}}`.

The numeric statuses this driver already maps become W3C error strings. The
mapping is not one-to-one and needs writing down rather than guessing — status 7
is `no such element`, 23 is `no such window`, 10 is `stale element reference`,
100 is `invalid argument`.

### 3a. Where this driver deliberately does NOT follow W3C

Three faults, and all three are inherited from WinAppDriver's measured wire
behaviour rather than chosen:

| status | our HTTP | W3C error name | W3C HTTP | agrees? |
|---|---|---|---|---|
| 7 no such element | 404 | no such element | 404 | yes |
| 9 unknown command | 404 | unknown command | 404 | yes |
| 13 unknown error | 500 | unknown error | 500 | yes |
| 100 invalid argument | 400 | invalid argument | 400 | yes |
| 101 invalid session id | 404 | invalid session id | 404 | yes |
| 105 element not interactable | 400 | element not interactable | 400 | yes |
| **10 stale element reference** | **400** | stale element reference | **404** | **no** |
| **23 no such window** | **400** | no such window | **404** | **no** |
| **19 `XPath Lookup Error`** | **500** | **`invalid selector`** | **400** | **no** |

`WebDriverFault` already says so for the second: *"HTTP 400, not 404, despite
being a 'not found' condition."* The third is not merely a wrong code —
`XPath Lookup Error` is not a W3C error name at all.

**None of these are ambiguous at request time**, which is what makes serving both
possible. The dialect is fixed when the session is created, so one fault renders
two ways. The fault is the domain concept; the status code and error string are a
projection of it — the same one-truth-two-consumers shape already used for XPath
and `/source`.

So `WebDriverFault` gains a W3C rendering beside its JWP one, rather than being
replaced or duplicated. W3C also requires a `stacktrace` member in the error
object, which may be an empty string.

### 4. Element references

| | key |
|---|---|
| JWP | `ELEMENT` |
| W3C | `element-6066-11e4-a52e-4f735466cecf` |

The value is the same string. Emitting **both keys** in one object is what several
drivers do and what keeps a mixed client working, and it costs nothing.

### 5. Routes that moved

| JWP | W3C |
|---|---|
| `/element/{id}/size`, `/element/{id}/location` | `/element/{id}/rect` |
| `/window_handle`, `/window_handles` | `/window`, `/window/handles` |
| `/keys`, `/touch/*`, `/moveto`, `/click`, `/buttondown` | `/actions` |
| `/execute` | `/execute/sync` |
| `/timeouts` `{"type":"implicit","ms":n}` | `/timeouts` `{"implicit":n}` |

**This is why pointer input should be built W3C-first.** `/actions` is where W3C
put every input primitive, and the JWP `/touch/*` and mouse routes become thin
wrappers over the same input path rather than a second implementation. Building
them the other way round produces exactly the duplication this design exists to
avoid.

## Order of work

1. The dialect seam: session creation, envelope, element key, error mapping.
2. Pointer input, W3C `/actions` first, JWP `/touch/*` as wrappers over it.
3. The moved routes, incrementally.

Step 1 is small and touches one filter plus session creation. Step 2 is the
30-test block measured on 2026-08-11. Step 3 can follow demand.

## What this does NOT change

The compatibility suite speaks JWP and remains the scoreboard. No W3C work may
cost a JWP test; the suite is run before and after each step, and a regression
there is a defect in the translation rather than an acceptable trade.

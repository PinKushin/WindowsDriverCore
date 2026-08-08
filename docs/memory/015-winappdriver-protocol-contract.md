# 015 — The WinAppDriver Protocol Contract

## Date: 2026-08-08

Harvested from `WinAppDriver/Docs/*`, `WinAppDriver/README.md`,
`WinAppDriver/Tests/WebDriverAPI/README.md`, and live probes against WinAppDriver
1.2.2009.02003 running on this machine. This is the specification a rewrite should be built
against. **Our current route table was derived from the W3C WebDriver spec, which is the wrong
document.**

## The contract is JSON Wire Protocol, stated explicitly

`Tests/WebDriverAPI/README.md`:

> These tests are written to verify each API endpoint behavior and error values as specified in
> [JSON Wire Protocol](https://github.com/SeleniumHQ/selenium/wiki/JsonWireProtocol) document.

That settles every ambiguity we have been guessing at. Where JWP and W3C disagree, JWP wins.

## The complete route table (`Docs/SupportedAPIs.md`, 60 rows)

Reproduced here because it is the acceptance contract. `:id` = element id.

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

### What we are missing

`window_handle`, `window_handles`, `location`, `buttondown`, `buttonup`, `click`, `doubleclick`,
`moveto`, all eight `touch/*`, `window/size` (both verbs), and every `window/:windowHandle/*` form.

### What we invented that WinAppDriver does not serve

`/window/rect`, `/element/:id/rect`, `/window/minimize`, `/window/restore`, `/window/handle`,
`/window/handles`. Harmless as extras, but they are why nobody noticed the JWP forms were absent.

### `window_handle` is the highest-leverage missing route

`Utility.CurrentWindowIsAlive` (`Tests/WebDriverAPI/AppSessionBase/Utility.cs:47`) is written so
that line 56 unconditionally overwrites the computed result with `true`. The **only** path to
`false` is `remoteSession.CurrentWindowHandle` throwing. Selenium 3.8 sends
`GET /session/:id/window_handle`; we 404; the client throws; the suite concludes the session is
dead and calls `TearDown()` + `CreateNewSession()` on **every** fixture init.

That is why our runs relaunch the app per test while WinAppDriver launches it once. One missing
route, multiplied across all 290 tests. `Tests/WebDriverAPI/README.md` guideline 4 confirms the
intended behaviour: "Reuse existing application session when possible to reduce unnecessary
application re-launching."

## Error envelope

From `Docs/FAQ.md`, WinAppDriver's own logged response:

```json
{"status":23,"value":{"error":"no such window","message":"Currently selected window has been closed"}}
```

JWP shape: **numeric top-level `status`**, plus `value.error` and `value.message`. Our
`Messages/ErrorResponse.cs` emits `{"value":{"error":…,"message":…,"stacktrace":…}}` with **no
`status` field**. Worth verifying against a live session whether Selenium 3.8 needs it to select
the right exception type — several tests catch `InvalidOperationException` specifically.

Measured live (probes against WinAppDriver, no session created):

- `GET /session/{bogus}/window_handle` → **HTTP 404, empty body**. No JSON envelope at all for an
  unknown session. We return a JSON `invalid session id` error instead.
- `GET /session/{bogus}/totally_unknown_route` → HTTP 404, empty body.
- `GET /status` → `{"build":{"revision":…,"time":…,"version":…},"os":{"arch":"amd64","name":"windows","version":"10.0.26200"}}`
  — **not** wrapped in `value`. Our `StatusRoutes` matches this shape. Note our route template is
  `"/status/"` with a trailing slash; confirm it still matches `GET /status`.

Canonical error strings live in `Tests/WebDriverAPI/AppSessionBase/CommonTestSettings.cs`
(`ErrorStrings` class) — that is the authoritative list, not our guesses.

## Locator strategies (`Docs/AuthoringTestScripts.md`)

| Client API | Strategy | Matches |
|---|---|---|
| FindElementByAccessibilityId | `accessibility id` | AutomationId |
| FindElementByClassName | `class name` | ClassName |
| FindElementById | `id` | RuntimeId (decimal, e.g. `42.333896.3.1`) |
| FindElementByName | `name` | Name |
| FindElementByTagName | `tag name` | **LocalizedControlType, upper camel case** |
| FindElementByXPath | `xpath` | Any |

Two defects this exposes in `ElementFinder.CreateCondition`:

1. **`tag name` semantics are wrong.** We map to `UIA_ControlTypePropertyId` integers.
   WinAppDriver matches **LocalizedControlType** — a localized string. Different property,
   different matching.
2. **The `_ => UIA_CustomControlTypeId` fallback is a bug.** `Element.cs:142`
   (`FindElementError_NoSuchElementByTagName`) and `:156` (`…ByTagNameMalformed`) both require an
   exception. Our fallback silently searches for Custom controls and can succeed. This is very
   likely part of the 23 "Assert.Fail failed. Exception should have been thrown" failures.

Note `FindElement_ByRuntimeId` (`Element.cs:64`) round-trips *our own* returned element id, so the
`.` vs `,` separator does not affect conformance — only hand-written ids copied from inspect.exe.

## Capabilities

`app`, `appArguments`, `appTopLevelWindow` (hex string, e.g. `0xB822E2`), `appWorkingDir`
(classic apps only), `platformName`, `platformVersion`. `app` value `"Root"` creates the desktop
session.

## Command line

```
WinAppDriver.exe                       # 127.0.0.1:4723
WinAppDriver.exe 4727                  # port only — single argument
WinAppDriver.exe 10.0.0.10 4725        # IP + port
WinAppDriver.exe 10.0.0.10 4723/wd/hub # base path rides on the port argument
WinAppDriver.exe * 4723                # bind all interfaces
```

Administrator is required **only** to listen on a non-default IP/port. Default loopback binding
runs unelevated — confirmed today, WinAppDriver started and served `/status` without elevation.

## System requirements, from the vendor

`Docs/FAQ.md`: "supported on machines running **Windows 10** (Home and Pro) and **Windows Server
2016**." `Tests/WebDriverAPI/README.md`: "Windows 10 version 1607 or later."

This independently confirms the floor computed in [[014-compatibility-and-deployment-targets]]:
Windows 10 1607 / Server 2016. Matching it exactly is sufficient; exceeding it is optional.

## Documented behaviours worth reproducing

- **Splash screens**: WinAppDriver often mistakes a splash screen for the main window, then fails
  with `no such window` / status 23 once it vanishes. Documented as a known defect with a
  `Thread.Sleep` workaround. An area where we could be better rather than bug-compatible.
- **Implicit scroll on select**: "As per the spec, we implicitly scroll elements within the view
  when they are selected." Matches the field observation in [[012-field-notes-from-driving-a-real-maui-app]]
  that a click moved an element 132px — it is documented behaviour, not a quirk.
- **Attaching to an existing window** via `appTopLevelWindow` for apps with no conventional
  launch path (Cortana) or very slow startup.

Relates to [[006-w3c-vs-json-wire-protocol]], [[013-architecture-audit]],
[[014-compatibility-and-deployment-targets]].

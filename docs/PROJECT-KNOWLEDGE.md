# WindowsDriverCore — Consolidated Project Knowledge

**Exported 2026-08-08, outside the repo, so it survives a from-scratch rewrite.**

This is everything learned across ~2 weeks and one long research session, condensed. It replaces
`docs/memory/001`–`016`. Read it before writing any code. It is written for someone who has read
the WinAppDriver docs but was not present for any of the debugging.

**How to use it:** sections 1–4 are the specification. Sections 5–7 are implementation knowledge
that was expensive to acquire. Section 8 is a list of mistakes not to repeat — both in the code and
in the reasoning.

---

## 1. What this project is

An open-source replacement for WinAppDriver, which Microsoft archived in June 2025. It speaks the
protocol Appium/Selenium clients expect and drives Windows apps through `IUIAutomation` COM.

The founding motivation was two WinAppDriver bugs:

- **#857** — elements exist on screen but are absent from the UIA tree `FindElement` searches;
  ListView items get orphaned.
- **#1079** — `FindElements` randomly returns empty.

Both are believed to come from the *managed wrapper's cached view* of the tree, not from UIA
itself. Querying the live tree with raw COM should make the whole class not exist. This is the
project's reason to be, and **it is still unimplemented** — do not lose sight of it while chasing
protocol parity.

**Direction from the user:** cheat-tool-level control. Raw COM, no managed wrappers hiding
behaviour, no hidden retries, no hidden caching, no hidden exception translation. Own the COM
pointers, the tree traversal, and the caching strategy. `unsafe` where it genuinely pays.

**FlaUI is not the answer** and this was settled with evidence: `Interop.UIAutomationClient` — the
only dependency — is authored by *Roemer*, FlaUI's author. It **is** FlaUI's interop layer. Adding
`FlaUI.UIA3` on top means wrapping a wrapper to fix wrapper bugs. The one useful idea from FlaUI is
that it draws a clear distinction between a pattern invoke and a real mouse click; take the idea,
not the dependency.

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

### Error envelope

```json
{"status":23,"value":{"error":"no such window","message":"Currently selected window has been closed"}}
```

Numeric top-level `status` plus `value.error` and `value.message`. Measured live against
WinAppDriver 1.2.2009.02003:

- unknown session → **bare HTTP 404, empty body**, no JSON at all
- unknown route → same
- `GET /status` → `{"build":{...},"os":{...}}`, **not** wrapped in `value`

Canonical error strings are in `Tests/WebDriverAPI/AppSessionBase/CommonTestSettings.cs`, class
`ErrorStrings`. Use that file verbatim; do not reverse-engineer strings from failures the way the
last implementation did.

### Locator strategies

| Client API | Strategy | Matches |
|---|---|---|
| FindElementByAccessibilityId | `accessibility id` | AutomationId |
| FindElementByClassName | `class name` | ClassName |
| FindElementById | `id` | RuntimeId |
| FindElementByName | `name` | Name |
| FindElementByTagName | `tag name` | **LocalizedControlType, upper camel case** |
| FindElementByXPath | `xpath` | Any |

Two traps:

1. `tag name` matches **LocalizedControlType** (a localized string), *not* `UIA_ControlTypePropertyId`
   integers. The old code mapped to control-type ids — wrong property entirely.
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

**"80/290 = 27.6%" was never a meaningful number.** WinAppDriver scores 112, and 112 is itself
depressed by a fixture defect. Both figures measure a broken harness.

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

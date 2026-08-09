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

## Not implemented

| Area | State | Notes |
|---|---|---|
| **XPath locator** | Every expression reported as `XPath Lookup Error` (status 19) | UIA has no XPath. WinAppDriver evaluates its own over the tree. Reporting valid expressions as invalid is wrong, but wrong *loudly* — silently matching nothing would look like a correct search. |
| **`DELETE /session` app shutdown** | Session is removed; the application keeps running | Leaks a process per session. Needs a process-termination path. |
| **Implicit wait** | `POST /timeouts` accepts and validates, then ignores | Find does not retry. This is the defect that cost the old implementation 167 tests, so it must not ship unfixed. |
| **Element interaction** | Not started | click, text, attribute, enabled, displayed, selected, clear, value. |
| **Element geometry** | Not started | `/location`, `/size`, `/location_in_view`. |
| **Window routes** | Not started | `window/size`, `window/{handle}/size`, `/position`, `/maximize`, `window_handle(s)`, switch, close. |
| **Mouse, touch, Actions** | Not started | `buttondown`/`buttonup`/`click`/`doubleclick`/`moveto`, eight `touch/*`. ~20 of the 70-test backlog is Actions **error validation** only, which needs no Actions implementation. |
| **Screenshots** | Not started | `/screenshot` for session and element. |
| **`/source`** | Not started | |
| **UWP `appArguments`** | Passed to activation, never verified | No test covers whether a packaged app receives them. |

---

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

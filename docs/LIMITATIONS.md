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
| **Element interaction** | Reads done; writes not started | `/text`, `/name`, `/attribute`, `/enabled`, `/displayed`, `/selected` and geometry all serve. `click`, `clear` and `value` do not. |
| **Element-valued attributes** | Render as `null` | `LabeledBy`, `ControllerFor`, `Selection.Selection` and the other properties whose UIA value is an element or element array. What WinAppDriver sends for these was never measured, and inventing a spelling would be a divergence written as if it were a contract. |
| **`/text` on a Selection** | Implemented, unmeasured | The rung that answers a list's selected item comes from WinAppDriver's own `ElementText.GetElementText` (`MinuteLoopingSelector.Text == "00"`), not from a measurement here. No Windows 11 app tried so far still exposes a looping selector in a reachable state. |
| **Issued-element ids grow unbounded** | Per session, until `DELETE /session` | `ElementRegistry` keeps one short string per element ever returned, which is what makes stale (status 10) distinguishable from unknown (status 7). A session that finds elements in a loop for hours accumulates them. Bounding it needs a policy, and a policy needs a measurement of what real suites do. |
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

**The remaining performance work, in order:**

1. Move the measurement into `bench/` under BenchmarkDotNet. The current
   instrument cannot resolve the effect it is being asked about.
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

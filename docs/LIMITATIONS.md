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

## The compatibility suite varies by about 9 tests run to run

**Measured 2026-08-10, by accident, which is the only reason it was noticed.**

Run 18 scored **124** against run 17's **133** and was read as a 9 test
regression from the change under test. It was not. `git fetch` and
`git reset --hard` had both failed with

```
fatal: detected dubious ownership in repository at 'C:/baseline/WindowsDriverCore'
owned by: BUILTIN/Administrators, current user: WIN10-BASELINE/tester
```

and the script piped them to `Out-Null`, so the run built **the same commit as
run 17** and scored nine tests lower.

**Identical binary, 133 then 124.** Every delta smaller than that is inside the
noise band, and several of today's were read as signal:

| Change | Delta | Verdict now |
|---|---|---|
| `/timeouts` | +62 | real |
| window read routes | +28 | real |
| Actions validation | +15 | real |
| guarded mouse rung | +8 | **within noise** |
| keys route | −1 | within noise |
| extended keys | 0 | within noise |
| element window-closed | +1 | within noise |

The large gains stand. The small ones were never distinguishable from variance,
and the per-test diff is the only thing that made any of them interpretable — it
showed run 16 to 17 changing exactly one test, and run 17 to 18 changing nine
with no code difference at all.

**What follows:**

- **Never read a delta under ~10 as an effect.** Use the per-test diff, and
  prefer a cause you can name over a number that moved.
- **The runner now aborts** if it cannot determine HEAD, rather than measuring an
  unknown commit. Silence on a failed checkout is how this cost a whole run.
- The likely sources are the applications, not the driver: the suite leaves
  Calculator, Notepad and Alarms running between runs, and a stale window changes
  what later tests find.

---

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

## The matched comparison: 19/290 against WinAppDriver's 281/290

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

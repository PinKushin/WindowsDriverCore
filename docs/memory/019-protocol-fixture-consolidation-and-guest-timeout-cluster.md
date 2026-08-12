# 019 — Protocol fixture consolidation, and a real timeout cluster on the guest

**Date:** 2026-08-11
**Status:** ALL 21 protocol fixtures converted and pushed (`cd8a91a`, `e8ee62c`,
`90252c9`). The guest timeout cluster (point 2) has been probed and traced to
its actual cause — it is NOT driver latency, see the update at the bottom.
Fixture-folding (point 3) still unstarted.

## 1. All 21 protocol fixtures now share one `WebApplicationFactory`

Originally 19 of 21 each booted their own per `[Test]`. Converted to
`[OneTimeSetUp]` + a per-test `[SetUp]` that rearms substitute defaults and
clears received-call history — verified green individually, green 3x in a
row for the full protocol project (185/185 unchanged each run, ruling out
order-dependence), and green once for the full solution. Protocol project
duration dropped from ~36-40s at the start of the day to a stable ~18-21s.

**The hazard that made the first 11 non-mechanical:** a test that
reconfigures a substitute's `.Returns()` inline, which a sibling test's
default silently depended on. Found by reading every file, not by
pattern-matching — confirmed live in four of the ten
(`ClosingAWindowWaitsForItTests`, `DeadWindowFailsFastTests`,
`PageSourceRouteTests`, `PointerStaysInsideTheWindowTests`).
Mutation-verified on the template file: removing one rearm line failed two
tests because a third, declared LAST in the file, ran before them — proving
NUnit's default execution order was never something to rely on.

**The remaining 7 files** (`CreateSessionRouteTests`,
`ElementActionRouteTests`, `ElementPropertyRouteTests`, `ElementRouteTests`,
`LongLivedSessionTests`, `OrientationRouteTests`, `SessionRouteTests`) use
`ISessionStore` (and two of them `IElementRegistry`) as REAL, unsubstituted
singletons. `ISessionStore.Clear()` and `IElementRegistry.Clear()` were
added — explicit test seams, never called by production code, called once
per test in `[SetUp]`. Two alternatives were considered and rejected for this
batch: NUnit `[Order]` (makes the leak deterministic rather than removing it,
and does nothing for the three `ElementXxxRouteTests` files, which seed a
session directly with no delete step to order around); self-cleanup via
`DELETE /session` (real API only, but circular for `SessionRouteTests`
specifically, which tests `DELETE /session` itself — a failing delete test
would fail to clean up and compound into whatever ran next).
`LongLivedSessionTests` was the sharpest case: every one of its 5 tests
asserts an EXACT `Registry.CountFor` after issuing 500-2000 element ids.

## Original survey, for reference

```
grep -rln 'WebApplicationFactory<WindowsDriverCore.Host.Program>' tests/WindowsDriverCore.Tests.Protocol/*.cs | wc -l
  21
grep -rln '[SetUp]' ... | xargs grep -l 'new WebApplicationFactory' | wc -l
  19
```

19 files construct a fresh in-process ASP.NET Core host per `[Test]`, via
per-test `[SetUp]` rather than class-level `[OneTimeSetUp]`. Integration tests
already solved the equivalent problem — `SharedDriverSession` launches
Calculator once and nine fixture classes share it. Protocol tests never got
that treatment.

**Correction made mid-investigation, worth keeping straight:** this is a
LOCAL concern only. Protocol fixtures never touch the Hyper-V guest — they
boot in-process. The guest exclusively runs the WinAppDriver compatibility
suite (vstest against the published binary). An earlier hypothesis conflated
the two; they are unrelated systems and any future reasoning about either
should keep them separate.

Two pieces of scoped, unstarted work:

1. Share one `WebApplicationFactory` per fixture class at minimum
   (`[OneTimeSetUp]` instead of per-test `[SetUp]`); investigate
   collection-level sharing across fixtures next, gated on whether
   per-test-substituted `NSubstitute` collaborators actually conflict when a
   factory instance is shared.
2. Fold single-purpose standalone fixtures into the suite class they
   exercise — e.g. a "close" test living in its own file should join the
   Calculator suite it tests, mirroring how `SharedDriverSession`-based
   integration tests already group by subject.

**Why it matters beyond speed:** thin per-manipulation test coverage is a
mutation-testing liability. Two fixes landed same day —
`SessionFactory.AttachToWindow`'s `0x`-prefix strip and
`SessionCapabilities`'s empty-`appTopLevelWindow` check — each got exactly one
test aimed at the one manipulation being verified. Untested branches in the
same methods (`_windows.Exists(handle)` failing, `GetHostedProcessId`
returning 0, `ownsApplication: false` on the attach path) have zero direct
assertions and will surface as Stryker survivors on the first real run.

## 2. A real ~8s timeout cluster on the guest, separate issue

Slowest tests from the `b6d6e1a2` guest run:

```
27,010 ms  Pen_LongClick
19,827 ms  Touch_LongClick
15,886 ms  Pen_Click_BarrelButton
10,200 ms  Touch_Click_OriginElement
 8,540 ms  GetElementDisplayedStateError_NoSuchWindow
 8,166 ms  ActionsError_StaleElement
 8,134 ms  GetElementTextError_NoSuchWindow
 8,105 ms  ClearElementError_ElementNotVisible
 8,083 ms  FindElement_ByAccessibilityId
 8,063 ms  ClickElementError_ElementNotVisible
 8,047 ms  GetElementAttributeError_StaleElement
 8,037 ms  GetElementTextError_StaleElement
 8,025 ms  GetElementScreenshotError_StaleElement
 8,022 ms  ClearElementError_StaleElement
 8,016 ms  SendKeysToElement_Alphabet
```

**UPDATE — probed, and the framing above was wrong.** Comparing per-test
durations against WinAppDriver's own reference run for the SAME test names
split this into two different things. Most of the "cluster" (the LongClick
tests, the `*_StaleElement` family) is comparable to or FASTER than
WinAppDriver once measured test-for-test — not a bug, partly explained by
`Thread.Sleep` calls baked into the test's own client-side helper code
(`AlarmClockBase.GetStaleElement()`), which cost both drivers equally.

Three tests were the real signal — an almost exact +8000ms tax where
WinAppDriver answers in 200-350ms: `GetElementDisplayedStateError_NoSuchWindow`,
`FindElement_ByAccessibilityId`, `ClickElementError_ElementNotVisible`.
Cross-referencing their exact TRX start/end times against the real request
transcript (not test-source guessing) showed all three windows contain the
identical sequence: `find AutomationId='AlarmButton' -> 0 match`, then
`AlarmPivotItem -> 0`, then `CloseButton -> 0`, each burning a full ~2.6s
implicit-wait cycle, before `CancelButton -> 1 match` finally succeeds.

This is `AlarmClockBase.TestInit()` — Microsoft's own cross-version
compatibility shim, which runs before every test and probes several possible
automation ids because different Alarms & Clock releases used different ones
for the same controls. When the app is on the main tab already, the first
candidate matches instantly. When it is stuck on the Add/Edit Alarm page
instead, three genuinely-absent ids each burn the full implicit wait before
the fourth succeeds. **Not driver latency — those 404s are correct.** The
open question is why the app was on the wrong page for these three tests
when WinAppDriver's reference run was not; two of the three windows open
right after a fresh session/launch. Full evidence chain in auto-memory:
`the-8s-cluster-is-a-page-recovery-cascade-not-slowness`.

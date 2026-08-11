# 019 — Protocol fixture consolidation, and a real timeout cluster on the guest

**Date:** 2026-08-11
**Status:** two findings, both unstarted work; nothing in this repo has changed for either yet

## 1. 19 of 21 protocol fixtures each boot their own `WebApplicationFactory`

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

Nothing in this suite legitimately takes 8-27 seconds. The tight clustering
right around 8000-8100ms across the `*_NoSuchWindow` and `*_StaleElement`
family is the signature of a fixed timeout budget being exhausted, not
organic variance — same shape as the pattern already recorded: *"a wait that
spends its whole budget never detected the thing"* (docs/LIMITATIONS.md, and
mirrored in auto-memory as `a-full-timeout-means-not-detected`). Read it as
looking for the wrong condition, never as a slow application.

**Total suite wall-clock time is stable across seven measured runs
(5:25-6:03), so this is not a whole-run slowdown.** It is specific tests
individually eating a timeout, which the user first flagged from the "feel"
of the VM running slow — the per-test durations bear it out even though the
total does not.

Not yet probed. Next step: pick one `*_NoSuchWindow` test (e.g.
`GetElementDisplayedStateError_NoSuchWindow`) and trace what condition it is
actually waiting on before assuming it is the same mechanism across the whole
cluster.

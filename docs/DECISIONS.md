# Decisions

Numbered, dated, and written in the owner's words where they gave them. A
decision made for a good reason is indistinguishable from an arbitrary one once
the conversation is gone, so the *why* lives here next to the *what*.

---

## 1. The compatibility suite cannot run in CI, and the guest stays the instrument

**Decided 2026-08-22.**

CI runs on `windows-latest`, which is Windows **Server** 2025 — no Microsoft
Store, no inbox UWP apps. 119 of 122 skipped integration tests are that one
cause: Calculator does not exist there. Three ways out were considered and all
three are closed.

**A self-hosted runner on the Win10 guest — REFUSED, on two independent
grounds.** The owner raised both:

> "i dont want a self hosted runner, those are not secure on public repos"

> "we'd also have to expose the VM to the internet, screwing up the test suite
> anyway, if we used it as a self hosted runner"

The first is GitHub's own guidance — a fork's pull request would run
stranger-authored workflow code on the host machine, and this repository is
public. The second is the stronger one and is specific to this project: the
guest is deliberately **offline**, and that is load-bearing. Networking it lets
the Store update its apps, which is exactly the confound that already cost a
baseline — Alarms & Clock updated mid-investigation on 2026-08-10 and renamed
`AlarmSaveButton` to `PrimaryButton`, making every score before and after it a
measurement of a different application.

**A Windows 10 hosted runner — does not exist.** Every x64 Windows image GitHub
offers is a Server SKU. Windows 10 reached end of support in October 2025, so
one is not going to appear. Booting the ISO inside a hosted runner needs nested
virtualisation, which the standard hosted runners do not expose.

**`windows-11-arm`, the only hosted CLIENT image — WRONG SUBJECT.** The owner
called it before the probe ran:

> "windows 11 is not going to work, the apps are not the same"

Measured, and already recorded in `CLAUDE.md`: **WinAppDriver itself scores
112/290 on Windows 11** against 280/290 on the Win10 guest. Same driver, same
suite; the entire difference is that Alarms & Clock is a different application
there. A suite run on Windows 11 measures app drift, not driver capability. A
probe workflow was written to test the image and was deleted unrun — the
question it would have answered is not the question that matters.

### What follows from this

**Two jobs that had been blurred are now separate.**

| | purpose | needs |
|---|---|---|
| the Win10 guest | **measurement instrument** | pinned app versions, offline, cold, reproducible |
| CI | **regression gate** | "does this still work at all"; version drift tolerable |

CI does not need to reproduce the guest measurement. It needs to stop tests
silently not running — which the skip-ratchet in `ci.yml` now guards, raised
from 23 to 50 once the build was green.

### The plan for a real conformance suite in CI

The owner's, recorded as stated:

> "we will just port the winappdriver suite to modern windows to do a conf suite
> in CI later, when we are passing the local windows written winappdriver Conf
> suite and can just basically change a few UIA controls to port the tests to
> modern windows, and can use it as our official conf suite in ci i guess."

So: **pass the Win10 suite first**, then port it — the work is mostly swapping
UIA control identifiers for their modern-Windows equivalents — and the ported
suite becomes the official conformance suite for CI. Sequenced that way round
deliberately: porting before passing would mean changing the target and the
driver at once, and no score would be attributable to either.

Ported, it also stops being a drift measurement and starts being a gate, which
is the job CI actually has.

---

## 2. The driver never invents a duration, and 50 ms is the ceiling when it must

**2026-08-22.** Two owner directions, one about who decides and one about how
much.

### Who decides how long a gesture takes

> "i figured when it came to dragging the consumer should say how long the drag
> will be, not the driver"

So `/actions` spends exactly the `duration` the client stated — no floor, no
padding. The multi-request `/touch/down` → `/touch/move` → `/touch/up` trio
carries no duration at all, and the client has ALREADY expressed the drag's
length by how far apart it sent the three requests. Anything the driver adds
there is added on top of a duration nobody asked for.

That leaves exactly one thing the driver must choose for itself: the gap between
the frames WITHIN one move, because a burst the system coalesces into a single
jump is not a gesture. It is a separation, not a duration, and it is named
`FrameSeparation` for that reason.

### How much, and why 50

> "80ms really, we cant get that down to like 50ms?"
>
> "when you cna get stuff to run in 50ms or less it appears instant to most
> human observers"

**50 ms, and the reason is perceptual rather than mechanical.** The measured
floor is lower — the sweep in `tools/probe-minimum-drag-pacing.ps1` puts the
threshold between 2 and 4 ms per frame — so the constant is set by where a delay
stops being noticeable, not by where the gesture stops working.

The 80 ms it replaced was **not a measurement**. It fell out of reusing the
`/actions` frame-interval constant as `10 frames × 8 ms`, which is an accident
of implementation. The two constants answer different questions and are now
deliberately independent:

| constant | answers | set by |
|---|---|---|
| `FrameInterval` | the RATE of a move whose length the client stated | a digitiser reports at 100–133 Hz; 8 ms is what the scheduler delivers free |
| `FrameSeparation` | the LENGTH of a move where nobody stated one | 50 ms, the perceptual threshold above |

### Why this belongs in a decisions log rather than only in a comment

This is the third value that constant has held — 300, then a nominal 50 that was
really 150, then a measured 80, now a measured 50 — and **two of the three were
defended with reasoning that had already been falsified**. The first cited
WinAppDriver's per-phase timing, gathered while timing was still believed to be
the CAUSE of the broken drag; once the cause turned out to be the injection API,
that number was evidence for nothing. The second reused a constant from an
unrelated argument. Both looked like measurements in the diff.

The rule that follows: **when the cause of a bug is refuted, every constant
justified by the old cause is unjustified**, whether or not it still works.
Sweep it again or say plainly that it is unmeasured.

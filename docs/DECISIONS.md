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

---

## 3. Flake is failure. It is never a reason to stop looking

**2026-08-22.** The owner, after reading a status that dismissed two regressions
as flappers:

> "flake is failure you know"

This restates a rule already in `CLAUDE.md` — *"Flake is a defect in
synchronisation or in the app — never noise"* — and it is recorded here because
the assistant broke it twice in one sitting while quoting it elsewhere.

### What was done wrong

`NavigateBack_ModernApp` fails in **8 of the last 10 guest runs**. It was
reported as "a long-standing flapper" and set aside. A test that fails 80% of
the time is a **failing test**; the 20% is what needs explaining, not what
excuses it.

`Pen_Scroll_Vertical` failed, then passed on a re-run, and that was used to
close the investigation — "flap, not attribution, so there is nothing to
explain". Wrong on the second half. The overrun MECHANISM was refuted by its own
measurement and that refutation stands on that evidence. But a test that moves
between runs at a fixed commit has a cause, and "it passed this time" is not it.

### The rule, stated so it cannot be read as advice

- **An intermittent failure is counted as a failure** in any score, backlog or
  status. It is not annotated away.
- **Variance is the evidence, not the excuse.** A test that changes verdict at a
  fixed commit is telling you there is state or timing the driver does not
  control. That is a driver defect until measured otherwise.
- **A re-run is for a second data point, never for a green result.** Re-running
  until it passes destroys the signal, which is the one thing the run was for.
- The two legitimate causes are **dirty start state** and **a race we cause**.
  Both are ours. "The machine was busy" is a hypothesis that has to be measured
  like any other, and on this project it has been wrong every time it was
  checked.

### Why it matters more here than elsewhere

The whole instrument is a 290-test score compared against a reference. A
category of failure that gets mentally subtracted before the comparison makes
the number mean less every time it is used — and this repository has already
published four scores that turned out to be measuring scaffolding rather than
the driver.

---

## 4. Parity includes determinism, not just the score

**2026-08-23.** The owner, on the goal for the project:

> "id really like to get to parity with winappdriver tonight if possible with
> zero flakke"
>
> "we dont have parity if we have flake, winappdriver doesnt have flake"

And, when the reference's own runs were shown to move slightly:

> "well it doesnt have flake in its own suite, when shit doesnt work it simply
> doesnt work."

### The standard, and the measured bar

**Matching 281/290 while three tests shift between runs is not parity.** A
client cannot build on a driver whose answer depends on the run. The reference's
failures are *deterministic* — when something does not work there, it does not
work every time — and that property is part of what is being matched.

**Measured, so the bar is a number rather than a slogan.** Across five
WinAppDriver runs on this guest, exactly three tests ever changed verdict:
`SendKeys_ModifierAlt`, `SendKeys_ModifierWindowsKey`,
`CompareElementsError_StaleElementParameter` — about one per run. Ours was
thirteen.

So the target is **not literally zero**, which the reference does not achieve
either, but **the reference's order of magnitude**: roughly one, against our
thirteen. Stating it as zero would make the goal unfalsifiable and invite
rounding a bad run down to "noise", which is the failure mode decision #3 exists
to prevent.

### What follows for how work is counted

- A fix is not banked until a **full** run confirms it, compared by test NAME.
  Three of the sixteen backlog tests are intermittent enough to swamp a two-test
  gain in a bare score.
- **A failure that always co-occurs with others is ONE defect.** Counting rows
  instead of columns inflated our flake list four-fold: what looked like eight
  independent flaky tests was two upsets, each taking four or five tests with
  it. Two investigations, not eight.
- Reducing flake and raising the score are the same work here, not competing
  priorities: the three scroll tests alone account for both the largest
  remaining variance and three of the sixteen backlog entries.

### Why this is recorded rather than assumed

The assistant reported two regressions as "known flappers" and moved on, and had
to be corrected. The instinct to discount an intermittent failure is strong
precisely because it is the cheapest reading available, and it is the reading
that made four earlier published scores meaningless.

---

## 5. Audit against the SPEC, and three clean passes before it counts

**2026-08-29.** After `/touch/flick` was found ignoring the protocol's own
`speed` parameter, the owner:

> "this likely means there missing stuff like this all over the place despite
> the fact we are really close to passing all the tests we can. so audit for
> that after you fix thius"

Right on both halves. The same sweep immediately found two more — `element/value`
reading only the JSON Wire array, and `POST /window` reading only `name` — each
of which breaks every Selenium 4 client while the compatibility score stays two
tests off its ceiling.

**Being close to passing is what made this invisible.** The suite is one
Selenium 3.8 client driving one application: every W3C request shape, every
parameter it never sends and every command it never calls is outside what it can
observe. A green score is not evidence about the protocol surface.

### The reference is the spec

> "we dont want to follow winappdriver anyway because its flakey, slow, and not
> up to date, but we do want to implement the winappdrivers api plus more"

So the audit compares against JSON Wire, W3C, and what real clients send —
never against WinAppDriver's implementation. Consequences worth stating because
they are easy to get backwards:

- **"WinAppDriver does not implement it either" does not close a finding.** The
  goal is its API *plus more*; a gap it shares is still ours.
- Its behaviour is **evidence, not authority**. Where it is measurably wrong,
  match the API and fix the behaviour.
- This differs from the TF2 salvage project's audit, which has the Source SDK to
  check against. Here there is no reference implementation worth copying, so the
  written protocol is the only authority.

### Three clean passes, three different lenses

`docs/PROTOCOL-AUDIT.md` carries the procedure. The standard:

**Not clean until three consecutive passes find nothing, one per lens, with no
code change between them.** By route, by parameter, by dialect. Any finding
resets the count.

Three *different* lenses rather than the same sweep three times, because a
static check repeated is one check — and the by-route pass is precisely what
skims over a missing parameter, since the route looks implemented. Only the
by-parameter lens found `speed`.

The reset after a fix is not ceremony: a fix is a change, and the third pass
exists to see the code as it will actually ship.

---

## 6. The audit's completion rule is "nothing NEW", and the local suite runs under the lock

Two corrections made on 2026-08-29, one to the audit and one to how I work. Both
recorded because the reason is what will be missing later.

### 6a. "Any finding resets the count" was unsatisfiable

Decision #5 above states the standard as *any finding resets the count*. That
cannot be met. As soon as one finding needs more than a route alias — a Platform
capability, or a decision the owner has to make — it stays open, every later pass
re-encounters it, and the count resets forever.

**A pass is clean when it finds nothing NEW.** An item already in the ledger is a
known gap, not a discovery.

The loophole is obvious, so two conditions travel with it:

- **An open item is recorded the moment it is found**, with what it needs.
  Otherwise "already known" becomes a way to launder anything inconvenient.
- **Clean means "no new gaps", never "no gaps".** A clean audit with six open
  items is an honest state. A clean audit reached by not looking is not.

Six open items are now written down in `docs/PROTOCOL-AUDIT.md` with what each
needs: `/location`, `/execute` and its `windows:` vendor commands, alert
handling, `/log`, the browser-shaped commands, and the DOM concepts that are
correctly refused.

**This does not weaken #5.** The bar it was reaching for — that a clean audit
means somebody actually looked — is unchanged, and the recording requirement is
what keeps it.

### 6b. Audit the API this project implements, not only the spec it aims at

Pass 6 replaced the instrument: instead of reading source, it **probed a running
server** with every endpoint in WinAppDriver's own `SupportedAPIs.md`.

Six of the gaps it found were in the reference's documented API rather than in
the W3C spec. `/window/:windowHandle/size`, `/position` and `/maximize` accepted
only the literal string `current`, so a client addressing a window by the handle
the path exists to carry got "Command not recognized".

Five passes of auditing outward at W3C had never checked the API this project
exists to implement. `tools/audit/Probe-Endpoints.ps1` carries the probe, because
a shell history is not a record.

### 6c. The local suite runs under `run-exclusive.ps1` — the owner's correction

I ran `dotnet test WindowsDriverCore.slnx` directly. One integration test failed
and passed on a re-run, and I was about to investigate the test. The owner:

> "you didnt run exclusive so you got stepped on in that last run"

Right. Re-running under the lock named the holder — another agent's DirectX
viewer, driving a window on the same desktop — and all three suites then passed.

**This repo's integration tests are desktop work**, not only the suite tagged
`Desktop`. They drive real windows and the UIA tree, so the whole-solution run
takes the machine-wide lock:

```powershell
C:\Users\pinku\source\repos\PinKushin\run-exclusive.ps1 -TimeoutMinutes 45 `
  dotnet test WindowsDriverCore.slnx --filter "TestCategory!=Comparison"
```

**The lock is also the diagnosis.** It prints the current holder's PID and
command line while waiting, so "why did that fail once" is answered by the wait
banner rather than by an investigation. Without it, a foreign foreground steal
and a real defect are indistinguishable.

### 6d. Red CI on a green desktop is a defect, not CI flake

Related and worth stating beside 6c, because both look "intermittent" and only
one is contention.

`DeletingByBackspace_ReadsAsEmpty_ImmediatelyAfter` passed here every run and
failed CI three runs in a row, at 3 of 5 attempts and then 1 of 5. It was a real
race in `WaitForValueToSettle`: two agreeing reads taken mid-stream are the poll
outrunning the application, not the application finishing.

**The slower machine is the more sensitive instrument** for this class of bug,
because the race is poll rate against the application's message loop. That
inverts the usual intuition that a fast dev machine is the harsh test.

An unlocked local run that fails once and passes on re-run is contention (6c). A
CI failure that repeats is a defect. Neither is ever answered by re-running for a
green result.

---

## 7. `/execute` serves the `windows:` vocabulary, minus arbitrary code execution

The owner, on the audit's open items:

> "yea we need to fix that mising api surface we are suppose to be a complete
> replacement for winappdriver"

Right, and the work landed — but the first thing it produced was a correction.

### 7a. The reference does not implement `/execute` at all

`docs/PROTOCOL-AUDIT.md` had recorded that WinAppDriver's vendor commands "ride
on `/execute`". That was asserted from memory and never checked, which is this
repository's signature failure — see `docs/FOUNDING-PREMISE.md`.

Measured against WinAppDriver 1.2.2009 in the guest, with an invented route as
the control:

```
CONTROL invented-route  -> 404      unrecognised
POST /execute           -> 501      ROUTED, not implemented
  windows: click / keys -> 501      same, whatever the script says
POST /execute_async     -> 404      not routed at all
POST /refresh, GET /url -> 501      ROUTED, not implemented
alerts, log, submit     -> 404
```

**501 versus 404 is the finding.** The reference routes three commands and
implements none of them; it has no `windows:` vocabulary whatsoever. That
vocabulary belongs to **appium-windows-driver**, the Node driver that WRAPS
WinAppDriver.

So this is not "closing a gap against the reference" — the reference has nothing
here. It is the *plus more* half of the goal, aimed at the Appium clients that
actually speak `windows:`. A 501 is a limitation rather than a contract, and the
project rule is to fix those rather than reproduce them.

The probe ships as `tools/vm/probe-does-winappdriver-serve-execute.ps1` so the
claim stays checkable.

### 7b. `windows: execPowerShell` is NOT served, and that is deliberate

appium-windows-driver has it. It runs a shell command on the machine hosting the
driver.

**That is remote code execution by design, reachable by anything that can open a
socket to this process** — and this driver binds a TCP port, with `*` as a
documented argument form for binding all interfaces. There is no authentication
on the wire, because the protocol has none.

Serving it would mean any process on the machine, and on the network when bound
broadly, could run arbitrary commands as the user running the driver. "Appium has
it" is not a reason; it is the same reasoning that would have this driver
reproduce every WinAppDriver defect for fidelity.

`ExecPowerShell_IsNotServed` asserts both that it is refused and that it is absent
from the supported list, so a later change adding it "for completeness" fails a
test rather than shipping.

**If it is ever wanted it needs an explicit opt-in switch**, off by default,
named so that turning it on is a decision rather than a default — and that is the
owner's call, not one to make while ticking off an API surface.

### 7c. What the vendor commands do NOT invent

Three refusals that could each have been a silent success, and each is the defect
this driver exists to fix:

- **A `pause` in `windows: keys` is refused.** This driver has no timed wait in an
  input path by rule. Delivering the keys untimed and answering 200 would report a
  timed sequence that was not timed.
- **A `windows: scroll` with no delta is refused** rather than dispatched as a
  no-op — which is also what distinguishes "I read your deltas" from "I ignored
  them", the exact way `/touch/flick`'s `speed` hid for the life of that route.
- **Malformed base64 in `setClipboard` is refused**, not pasted literally. Falling
  back to the raw string would put `not!base64` on the clipboard when the caller
  asked for whatever it decoded to.

And raw JavaScript is refused **with a reason and the vocabulary** — a UIA tree is
not a document, so there is nothing for a script to run against. WinAppDriver
answers a bare 501 with no body.

### 7d. The mouse wheel was missing entirely, and `/actions` was hiding it

`windows: scroll` needed a wheel, and `IPointerInput` had none — no
`MOUSEEVENTF_WHEEL` anywhere in the codebase.

Which surfaced a third silently-skipped input source: W3C's `/actions` defines a
**`wheel`** source type alongside `pointer` and `key`, and this driver skipped
anything that was not a pointer. `key` sources were found the same way one pass
earlier. The pattern is worth naming: **`/actions` is one route with three
independent implementations behind it, and two of the three were missing while
the route answered 200.**

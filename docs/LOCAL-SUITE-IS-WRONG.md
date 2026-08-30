# The local test suite needs rebuilding, and here is the evidence

**Written 2026-08-30 at the owner's direction, after a night in which the suite
cascaded twice, left applications on the desktop, and I asserted three things
that were false.** It exists so a refactor starts from measurements rather than
from the memory of a frustrating night.

> "this project is simply at the point that im going to need a massive refactor
> again, because basic functions were not done right"

---

## What is measured

### 1. Almost nothing exercises what ships

| tests | share | what they test |
|---|---|---|
| 56 | 26% | finding elements |
| 47 | 21% | acting on elements |
| 42 | 19% | reading elements |
| 40 | 18% | app lifecycle — launch, close, attach, instance, leak |
| 18 | 8% | unclassified |
| **11** | **5%** | **through the real server** |

**Thirty of 44 fixtures construct internals** — `new UiaElementFinder`,
`new ApplicationLauncher`, `new WindowLocator`. The shipped artefact is a CLI
server speaking HTTP. A test that news up `UiaElementFinder` cannot see session
lifetime, contamination, teardown or the input drain, because none of those live
there — and those are exactly the defects that took all day to find on the guest.

### 2. Constructing internals is WHY it launches so much

The owner's diagnosis, and it is correct:

> "if you are constructing internally that explains why the fuck you are booting
> a new app each time, you cant use a shared fixture if you do that"

A fixture that builds its own launcher has nowhere to put a shared session, so it
launches, uses, and kills. Measured at the start of the night: **30 launches for
4 distinct applications**, 22 of them Calculator across 10 fixtures.

### 3. Kill-by-name makes sharing impossible

Eleven fixtures call `AppLifetime.KillAll(<process name>)`. That matches every
process of that name — the instance another fixture is sharing, and a
developer's own copy.

**This is what cascaded.** Five fixtures share the WPF/Win32 subject; three kill
it by name; alphabetically the killers sit in the middle:

```
LadderAgainstOwnSubject   shares
PackagedAppAttaches*      KILLS BY NAME
RuntimeIdIsAbsolute       shares  -> subject gone
SendKeysSettles*          shares  -> subject gone
SessionTracks*            KILLS BY NAME
TheDrainWorksMoreThanOnce shares  -> subject gone
TouchInjection            KILLS BY NAME
XPathAgainstOwnSubject    shares  -> subject gone
```

Eight Calculator fixtures do the same with `KillAll(Calculator)` — which is why
grouping them by subject cascaded a second time. **Sharing a subject with a
killer in the room is not sharing.**

### 4. Teardown relies on a fallback that should never run

`ApplicationTerminator` posts `WM_CLOSE`, then escalates to `CloseMainWindow`,
then `Kill`, with a 2-second grace at each step. On the guest the polite close
does the work — `terminate pid ... -> ended 40.6 ms`, `113.7 ms`, `99.9 ms`.

Locally it did **not**: our own `TestApp` deliberately refuses `WM_CLOSE` while
its edit box has text, and two fixtures typed into the shared subject and walked
away. So the Kill became load-bearing, and for those seconds a modal dialog owned
the foreground on a desktop several suites share.

**A fallback that is load-bearing is not a fallback.** One instance is fixed. The
owner's position is that there are more, and nothing here contradicts that —
there is no assertion anywhere that a close was answered politely.

---

## The order a rebuild has to follow

Learned by getting it wrong twice in one night:

1. **Eliminate every kill-by-name.** Until then no sharing is safe, and any
   attempt at it cascades.
2. **Group by subject.** One application, one setup, every class about that
   subject sharing it — the shape the compatibility suite already uses with
   `CalculatorBase`.
3. **Drive through the real server.** The 5% → most-of-it change, and the one
   that would have caught the night's defects.

Doing 2 before 1 is what produced both cascades.

---

## What I could not see, and why that matters for the next attempt

The owner watched the screen; I read greps and logs. Everything I got wrong
tonight, I got wrong that way:

- **"Two fixtures launch per test."** False. My detector treated everything after
  the last `[SetUp]` in a file as being inside it.
- **"An interrupted run leaked processes."** False. That was the suite running.
- **"A window-less `CalculatorApp` is a corpse."** False. Normal for a packaged
  app, whose window belongs to `ApplicationFrameHost`.
- **"The XPath cascade was `MenuModeTests`' modal loop."** False. The modal loop
  was real and separately fixed; the cascade was kill-by-name.

This is the repository's own rule about UI claims, met the hard way: **anything
about a running desktop that cannot be verified by looking at it is a question,
not a statement.** A rebuild should assume the same and put assertions where the
eye currently is — a teardown that fails when a close is refused, a leak test
that fails when a subject outlives its session.

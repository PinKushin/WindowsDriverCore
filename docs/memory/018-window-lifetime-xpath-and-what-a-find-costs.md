# 018 — Window lifetime, XPath, and what a find actually costs

**Date:** 2026-08-10
**Status:** measured; one item still under measurement at time of writing

## 1. A session's window is not permanent

At the moment the launcher returns, a packaged application has **no
`ApplicationFrameWindow`**. Measured across three cold starts:

```
waiter handed : 0x00210CE6  class=Windows.UI.Core.CoreWindow
GA_ROOT of it : 0x00210CE6  class=Windows.UI.Core.CoreWindow   <- itself
finds from it : num5Button=1  buttons=47                       <- all fine
```

The `CoreWindow` is top-level, is its own root, and finds from it work. Later,
when the application is rehosted into its frame, that CoreWindow is **destroyed**
— not reparented — and every command answers "Currently selected window has been
closed". A warm application already has its frame, which is why this only ever
appeared on a cold start.

**Three fixes at attach time all failed identically.** Preferring
`FindFrameWindowHosting`, rejecting a CoreWindow and waiting for its frame, and
resolving with `GetAncestor(GA_ROOT)` each asked for a window that does not exist
yet, returned 0, ran the poll loop to its deadline, and handed back window 0.
That presents as "element finds come back empty", and one test took 30 seconds —
a timeout, not a slow find.

**The fix belongs at use time.** `RequiresSession` re-resolves when `IsWindow`
says the handle is dead. Two properties matter as much as the fix:

- a **live** handle is never second-guessed, or a client that switched windows
  deliberately silently loses its choice
- a re-resolve that finds nothing **keeps the dead handle**, so the driver reports
  the window is gone rather than inventing one

## 2. A dead window must fail fast — but only when it is really dead

Making the window "exist" more often exposed a latent ordering bug. Measured with
the application killed underneath a live session:

```
find, window alive             775 ms  status=0
find, window alive, no match  2673 ms  status=7   (implicit wait, correct)
find, window DEAD             2578 ms  status=7   <- wrong on both counts
ten dead-window finds        25443 ms
```

The liveness check ran **after** the retry loop, so a dead window was searched 50
times at 50 ms before anyone asked whether it existed. A cold-start compatibility
run went from ~6 minutes to **over 30 without finishing**. It also answered "no
such element", sending a client hunting for a better locator for something no
locator can reach; 24 suite tests expect "no such window", status 23.

Moving the check ahead of the retry fixed the time — the run completed in **3m
25s** — and recovered 25 tests.

**But it cost 19** — and an isolation run attributed them, correcting the guess
that was written here first. A build with the re-resolve kept and the fail-fast
disabled scored 122:

```
                    gained  lost   net
neither fix                        118
re-resolve alone      23     19     +4   -> 122
fail-fast on top       2      0     +2   -> 124
```

**The fail-fast is pure gain.** The suspicion that it was too aggressive on cold
start was wrong. **All 19 belong to the re-resolve**, and the lost set is tests
asserting a specific count or first match — which is what moving the search root
does. The re-resolve points the session at the `ApplicationFrameWindow` where it
began at the `CoreWindow`, and the frame is a superset (73 descendants against 65,
measured) because it also holds the title bar and chrome.

So the re-resolve should prefer the hosted CoreWindow — the same *kind* of window
the session started with — rather than the outermost frame. To be confirmed by
comparing find results from each on the same application before changing it.

## 3. XPath: use the BCL engine, own only the lifetime

**Recorded from WinAppDriver 1.2.1, 38 expressions** — its XPath is essentially
**complete XPath 1.0**. The tell that it was not hand-rolled:

```
//Button[1]     matched 8
(//Button)[1]   matched 1
```

Eight is correct — `//Button[1]` is "every Button first among its siblings" — and
it is the subtlety hand-written evaluators fail. So they projected the tree to XML
and used `System.Xml.XPath`.

**The reported flakiness is therefore not the evaluator; it is the snapshot's
lifetime.** A correct engine over a stale document gives correct answers about a
tree that no longer exists, which is issue #1079.

Ours uses the same engine over a projection built **inside a single request and
never held** — a local, not a field. Correct semantics inherited for free;
staleness structurally impossible rather than avoided by discipline.

The regression test that matters is `//Button[1]` versus `(//Button)[1]` plus one
stepped path: a **flat** projection still answers `//` correctly, so only a
per-parent index or a stepped path distinguishes nested from flat. Verified by
mutation.

Already worth 5 suite tests on the cold run before any tuning.

## 4. What a find costs

Calculator, 65 nodes, on the Windows 11 host:

```
ElementFromHandle                    0.85 ms
FindAll descendants, TrueCondition  12.92 ms  (65 elements)
FindAll descendants, Button cond    12.53 ms  (47 elements)  <- narrowing saves 3%
FindAll children,    TrueCondition   1.50 ms  (2 elements)   <- 8.6x cheaper
FindFirst, early exit                0.79 ms                 <- 16x cheaper
FindAll + 47 GetRuntimeId           11.60 ms                 <- id reads are FREE
FindAll + 5 properties, live        47.92 ms
FindAllBuildCache + 5 properties    27.20 ms                 <- cache wins at 5
step every node with a walker       32.31 ms                 <- 2.33x the bulk walk
whole driver FindAll                14.81 ms                 <- our layering ~2.6 ms
```

**Traversal dominates, not marshalling.** The cache request **wins at five
properties (1.76x) and loses at one (0.91x)** — both earlier claims in
`LIMITATIONS.md` were wrong, in opposite directions, from generalising a
single-property measurement.

Two API traps, both presenting as the same misleading error:

- **`RuntimeId` cannot be cached.** `AddProperty(30000)` is accepted, then
  `GetCachedPropertyValue` throws `E_INVALIDARG`.
- **A cache request's `TreeScope` must include `TreeScope_Element`.** With
  `Descendants` alone the results' own properties are never cached and every read
  throws `E_INVALIDARG` — which reads exactly like "not cacheable" and is not.
  `TreeScope_Subtree` returns shape *and* properties in one crossing, after which
  `GetCachedChildren` walks in-process.

## 5. The guest agent

It failed twice in ways invisible from outside, so it now lives in
`tools/vm/agent.ps1` rather than only in the VM: a **heartbeat** file so session 0
can tell a live agent from a hung one, a `.done` file carrying the child's **exit
code and status** so an interrupted job stops reading as a finished one, a global
**mutex** so two agents cannot race the same queue, and the loop wrapped as well
as its body so an escaping error restarts it instead of ending it while `-NoExit`
keeps the window looking alive.

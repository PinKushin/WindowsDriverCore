# WindowsDriverCore

**The WinAppDriver API, reimplemented on raw `IUIAutomation` COM, built to
FlaUI's standard of capability.**

Point an existing Appium or Selenium suite at it unchanged and stop hitting the
ceiling.

> **Status: incomplete.** A rewrite is in progress on `feat/rewrite-jwp-core`.
> Sessions, element find and every element *property* work. Click, keyboard
> input, window management, Actions, XPath and screenshots do not yet. See
> [what works](#what-works) before deciding whether it is useful to you.

---

## Why this exists

Three facts define the gap:

- **FlaUI reaches UI Automation properly.** It is pattern-aware, and it draws an
  explicit distinction between invoking a UIA pattern and dispatching real mouse
  input.
- **But FlaUI is a .NET library.** It cannot be driven from an Appium suite, from
  Python, or from any existing test that speaks WebDriver. Reaching for it means
  abandoning the protocol.
- **WinAppDriver has the API every existing suite already speaks** — and an
  implementation that is both weak and, since June 2025, archived. Nothing filed
  against it will ever be fixed.

So this is not a FlaUI wrapper and not a faithful reimplementation of
WinAppDriver's limitations. It serves WinAppDriver's protocol over a UIA layer
as capable as FlaUI's.

It depends on `Interop.UIAutomationClient` — FlaUI's own interop layer, the raw
COM surface, written by FlaUI's author — and deliberately **not** on
`FlaUI.Core`. A peer, not a wrapper.

**The contract is JSON Wire Protocol, not W3C WebDriver.** Where the two
disagree, JWP wins; that is what WinAppDriver's own test suite asserts. The
previous implementation was built from the W3C spec, and that single wrong choice
produced a large share of its failures.

---

## Measured

Numbers here were produced by running something. Where conditions were not
matched, it says so.

| | Result |
|---|---|
| A property read, before and after handle caching | **19.40 ms → 0.45 ms** (43.5x), num5Button, 20 samples |
| An element find, this driver vs WinAppDriver | ~33 ms vs ~1070 ms — **unmatched conditions**, see caveat below |
| WinAppDriver's score on its own compatibility suite, Windows 11 | **112 / 290** |

**The find comparison is a signal, not a benchmark.** This driver ran in-process
while WinAppDriver ran over HTTP and re-resolved its element each iteration. It
is worth chasing in `bench/`, not worth quoting as a headline.

**112/290 matters more than it looks.** WinAppDriver fails 178 of its own tests
on Windows 11 — mostly because the applications they drive have changed. Parity
is a much smaller target than the suite size suggests.

---

## What works

| Area | State |
|---|---|
| `GET /status`, unknown-command fallback | ✅ |
| Session create / list / delete | ✅ |
| `GET /orientation` | ✅ |
| Element find — `POST /element`, `/elements` | ✅ `accessibility id`, `class name`, `name`, `id`, `tag name` |
| Element properties — `/text`, `/name`, `/attribute/{name}`, `/enabled`, `/displayed`, `/selected` | ✅ |
| Element geometry — `/location`, `/location_in_view`, `/size` | ✅ |
| Stale vs unknown element (status 10 vs 7, including its destructive first-touch behaviour) | ✅ |
| Application launch — classic exe and packaged AUMID | ✅ |
| CLI argument forms and base path | ✅ |
| **Click, `/clear`, `POST /value`, keyboard input** | ❌ next |
| **Window routes, mouse / touch / Actions** | ❌ |
| **XPath, implicit wait, screenshots, `/source`** | ❌ |
| **`DELETE /session` shutting the application down** | ❌ leaks a process per session |

[`docs/LIMITATIONS.md`](docs/LIMITATIONS.md) is the live list, including the
deliberate divergences and the things the tests cannot prove.

---

## Running it

```bash
dotnet run --project src/WindowsDriverCore.Host
```

WinAppDriver's argument forms are accepted unchanged:

```bash
WindowsDriverCore.exe                        # 127.0.0.1:4723
WindowsDriverCore.exe 4727                   # port only
WindowsDriverCore.exe 10.0.0.10 4725         # host and port
WindowsDriverCore.exe 10.0.0.10 4723/wd/hub  # base path rides on the port argument
WindowsDriverCore.exe * 4723                 # all interfaces
```

Build and test:

```bash
dotnet test WindowsDriverCore.slnx
```

---

## Design, in short

```
src/WindowsDriverCore.Host        composition root, CLI, DI
src/WindowsDriverCore.Protocol    JWP surface — routes, envelopes, faults. No UIA.
src/WindowsDriverCore.Automation  element find and inspection. Typed in, typed out. No HTTP.
src/WindowsDriverCore.Platform    Win32, window discovery, process lifetime
```

`Automation` and `Platform` do not reference ASP.NET Core, and that is enforced
by project references rather than by convention. **The automation layer is a
usable .NET library on its own** — the HTTP server is an adapter over it.

Speed is a design goal, not a later concern. Round trips dominate, because this
is cross-process COM, so the work is in making fewer of them: hold the element
the caller already named, fetch more per trip, own COM lifetime explicitly
rather than leaving it to finalizers.

**Compatibility floor is Windows 10 1607 / Server 2016**, matching
WinAppDriver's, so GitHub's Windows runners are in scope.

---

## How this repository treats claims

**A claim written in this repository is not evidence.**

Wire behaviour comes from recordings captured against the real WinAppDriver, not
from reading the specification. Five load-bearing claims here turned out to be
wrong, each inherited from an earlier session and repeated without being checked;
[`docs/PROJECT-KNOWLEDGE.md`](docs/PROJECT-KNOWLEDGE.md) §0 lists them and what
each one cost.

The most expensive was a rule — *never hold an element between calls, it goes
stale* — which forced a tree walk on every command and was refuted by one
experiment. **Doctrine is the easiest kind of claim to inherit, because it does
not look like a claim.**

Two specific things this project does **not** claim:

- **It does not fix WinAppDriver issue #857.** Those elements are absent from the
  UIA tree entirely; Inspect.exe cannot see them either, so no client can. See
  [`docs/FOUNDING-PREMISE.md`](docs/FOUNDING-PREMISE.md).
- **The capability claim is the one with evidence.**
  [`docs/CLICK-SEMANTICS.md`](docs/CLICK-SEMANTICS.md) has a documented cause, a
  reproduction, and a measured before/after from a real application suite.

---

## Documentation

| File | What it is |
|---|---|
| [`docs/PROJECT-KNOWLEDGE.md`](docs/PROJECT-KNOWLEDGE.md) | The single consolidated briefing — protocol contract, route table, measured ground truth, UIA/COM knowledge, mistakes not to repeat |
| [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md) | Live list of what does not work, and why |
| [`docs/CLICK-SEMANTICS.md`](docs/CLICK-SEMANTICS.md) | The pattern ladder, and the field evidence for it |
| [`docs/FOUNDING-PREMISE.md`](docs/FOUNDING-PREMISE.md) | The two bug reports this project was founded on, and how both were misdescribed |
| [`docs/REWRITE-SPEC.md`](docs/REWRITE-SPEC.md) | Architecture and conventions |

---

## Licence

MIT. See [`LICENSE.txt`](LICENSE.txt).

Not affiliated with Microsoft. WinAppDriver is Microsoft's, archived June 2025;
FlaUI is Roemer's and is a peer project rather than a dependency.

# Documentation index

Read in this order if you are new to the project.

| Document | What it is | When to read it |
|---|---|---|
| **[PROJECT-KNOWLEDGE.md](PROJECT-KNOWLEDGE.md)** | The consolidated briefing. Protocol contract, compatibility floor, measured test results, UIA/COM knowledge, app drift, mistakes not to repeat. | Before writing any code. |
| **[FOUNDING-PREMISE.md](FOUNDING-PREMISE.md)** | What this project actually fixes, written after finally reading the two issues it was founded on. Both were misdescribed here for two weeks. | Before repeating any claim about #857 or #1079. |
| **[CLICK-SEMANTICS.md](CLICK-SEMANTICS.md)** | The design requirement for element click, with the field evidence. The project's one demonstrable advantage. | Before implementing click. |
| **[REWRITE-SPEC.md](REWRITE-SPEC.md)** | Architecture, coding conventions, test strategy, build order, branching. | Before starting a milestone. |
| **[LIMITATIONS.md](LIMITATIONS.md)** | Live list of what is not done, what the platform forbids, and what the tests cannot prove. | Before claiming anything works. |

## Supporting material

| Path | What it is |
|---|---|
| `tests/WindowsDriverCore.Tests.Protocol/Recordings/` | Raw responses captured from real WinAppDriver. **The wire contract.** Re-record; never hand-edit. |
| `memory/` | The original per-topic entries, 001–016. Superseded by `PROJECT-KNOWLEDGE.md` and kept as an audit trail — several record corrections to their own earlier claims. Where they disagree with the consolidated doc, the consolidated doc wins. |
| `plan-raw-com-migration.md` | Historical. The plan for the first implementation, now superseded by the rewrite. |

## Outside this repository

| Path | What it is |
|---|---|
| `../WinAppDriver/` | Microsoft's unmaintained repo (not archived): the 60-route API list, the docs, and the compatibility suite. Kept **pristine** — local edits are stashed, not committed. |
| `../PokemonBattleJournal/` | A real MAUI application with a Windows Appium suite. The source of the click-semantics field evidence, and a candidate test fixture. |
| `~/.claude/projects/…/memory/` | A mirror of `PROJECT-KNOWLEDGE.md` outside the repository, so it survives a from-scratch rewrite. |

## The habit this documentation exists to enforce

Three load-bearing claims in this repository turned out to be wrong, and every
one of them was inherited from an earlier session and repeated without checking:

1. What issues #857 and #1079 actually say — nobody had read them.
2. That the field report of "clicks failing in a CollectionView" was #1079 — it
   was a click-path problem with a different cause entirely.
3. That WinAppDriver's failures came from a cached view of the tree — inference,
   presented as fact, and the entire justification for the architecture.

A fourth was caught in the other direction: overstating the driver's fault for
something that was really a consequence of building custom MAUI controls.

**A claim written in this repository is not evidence.** Everything in the
recordings, the measured numbers, and the limitations file was produced by
running something. Everything else should be treated as a hypothesis until it
has been.

# Stryker notes

**Run it from a test project directory. Never bare from the repository root.**

```bash
cd tests/WindowsDriverCore.Tests.Unit      && dotnet stryker   # mutates Automation
cd tests/WindowsDriverCore.Tests.Protocol  && dotnet stryker   # mutates Protocol
```

Both are headless. Neither touches the desktop.

## Why the config lives in each test project, and not at the root

Stryker reads `stryker-config.json` from the **working directory**. A config at
the repository root is read only when Stryker is invoked there — and invoking it
there is the thing that must not happen.

## `test-projects` at the root did not scope anything, and said nothing

Measured 2026-08-09:

| Invocation | Tests discovered |
|---|---|
| `dotnet stryker` from the repository root | **294** — every test project, Integration included |
| From a test project directory with `--project` | **87** — that project only |

The root config listed only the Unit and Protocol projects under `test-projects`.
It made no difference. Stryker discovered every test project in the tree,
including `Tests.Integration`, which launches Calculator and Settings — so a
mutation run would have driven the desktop once per mutant.

Nothing warned. The run looked normal. **This was claimed as safe twice before it
was measured**, which is the whole reason this file exists: the protection was
configuration-shaped and did nothing, and only a test-count check revealed it.

**Check the count.** `Number of tests found: N` in the early output is the
verification. 87 is Automation via the unit tests; 106 is Protocol. Anything near
294 means the scoping failed and the desktop is about to be driven.

## First results, 2026-08-09

| Project | mutants | before | after |
|---|---|---|---|
| Protocol | 302 | 63.68% | **67.95%** |
| Automation | 635 | 17.96% | **41.69%** |

**Neither number is a quality score, and Automation's especially is not.** These
runs use the headless suites only. `UiaElementFinder`, `UiaElementInspector`,
`UiaElementInteractor` and `UiaElementResolver` are covered by the integration
suite, which is excluded, so their mutants report `NoCoverage` and drag the
figure down without meaning anything about them. Read the per-file survivors,
not the headline.

Two real gaps came out of the first run and are now closed:

- **The nested-find routes had no protocol tests.** 18 `NoCoverage` mutants in
  `ElementRoutes.cs`. They had integration tests, which do not run here, so the
  route behaviour — scope, the empty-array asymmetry, registry recording — was
  unguarded at the protocol level.
- **`UiaProperties` had no test at all**: 157 survived, 0 killed.

**What was deliberately not done about the property table.** The obvious fix is
to assert all ~150 ids. That is a change-detector test — it fails whenever the
table is edited and detects no defect, because the assertion is a copy of the
thing it checks. Instead the tests assert what a wrong id actually violates:
distinctness (two names on one id means one silently reads the wrong property),
range (a transposed digit lands outside it — Microsoft's own tables contain two
such typos), the twenty ids measured end to end against real WinAppDriver, and
case sensitivity.

That killed most of the 157 anyway, which is the point: the property worth
asserting was cheaper *and* stronger than the enumeration.

## Integration results, 2026-08-09 — 141 tested, 49.00%

| file | killed | survived | no coverage |
|---|---|---|---|
| `UiaElementFinder` | 35 | 7 | 3 |
| `UiaElementInspector` | 18 | 6 | 7 |
| **`UiaElementInteractor`** | 12 | 9 | **41** |
| `CachingElementResolver` | 11 | 14 | 6 |
| `UiaElementResolver` | 8 | 4 | 1 |
| `UiaRuntimeId` | 12 | 3 | 1 |

**The finding is the 41, not the 49%.** Calculator is buttons with
`InvokePattern` and nothing else, so every click test exercises exactly one rung
of the ladder. Toggle, SelectionItem, ExpandCollapse, Focus-for-Edit and the
three-level ancestor walk are unreached — and the ancestor walk is the rung with
the field evidence behind it, the one that fixed the MAUI `CollectionView`.

A suite can be green, mutation-tested, and still have never run the feature the
project exists for. That is what "read the survivors, not the score" means here:
49% would have looked like a middling result to improve, when the actual message
was that one file's worth of behaviour had no test subject at all.

## Read "N total mutants will be tested", never "N mutants created"

`635 mutants created` is printed **before** the `mutate` filter is applied. A
scoped run and an unscoped run both report it, so it says nothing about scoping.

Proved with a control: `"mutate": ["zzz/definitely-not-a-file.cs"]` also reports
`635 mutants created`, then `Stryker was unable to calculate a mutation score` —
only explicable if filtering happens after that line. A single-file glob reports
635 created and 15 tested.

The line that means something is `N total mutants will be tested`, alongside
`N mutants got status Ignored. Reason: Removed by mutate filter`.

**This cost a killed run.** A scoped integration run was cancelled on the
assumption that 635 meant the globs had failed. Both checks that settled it were
free and headless.

## Path globs are project-relative

`mutate` patterns resolve against the **mutated project's** directory, not the
solution root and not the invocation directory. A glob written from the
repository root matches nothing.

Measured: `"mutate": ["src/WindowsDriverCore.Automation/**/*.cs"]` produced 47
mutants across files it had marked `Excluded`, then reported "Stryker was unable
to calculate a mutation score" after eight minutes.

- All globs wrong → zero mutants → loud failure.
- **One glob wrong → a real run, a plausible score, silently measuring less.**
  That one needs a mutant-count or `Excluded`-count guard; a `NoCoverage` check
  cannot see it, because a file excluded by a glob is never mutated and so never
  appears in any status bucket.

## `TargetFramework` must be literal in every csproj

Buildalyzer, which Stryker uses to discover projects, reads the project file
**textually**. A `TargetFramework` inherited from `Directory.Build.props` is
invisible to it, and so is `$(SomeProperty)` indirection — both measured. The
symptom is `No project found` after about a second, with `Analyzing 0 projects`
in the debug log and no reason given.

`Directory.Build.props` carries a comment saying not to move it back.

## Interrupting the command does not stop the run

Cancelling the launching call leaves `dotnet stryker`, `vstest.console` and
`testhost` running, and they keep launching applications. After stopping any run,
kill explicitly and then verify the list is empty — the first sweep has missed
processes that were still spawning.

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object { $_.CommandLine -match 'stryker' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-Process CalculatorApp,SystemSettings,testhost,vstest.console -ErrorAction SilentlyContinue | Stop-Process -Force
```

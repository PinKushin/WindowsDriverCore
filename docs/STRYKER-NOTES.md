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

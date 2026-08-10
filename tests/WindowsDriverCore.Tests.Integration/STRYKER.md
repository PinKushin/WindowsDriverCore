# Mutating against the integration suite

```bash
cd tests/WindowsDriverCore.Tests.Integration && dotnet stryker
```

**This drives the desktop.** Calculator and Settings launch once per mutant.
Run it on a machine nobody is using, and expect tens of minutes.

## `concurrency` is 1, and it is not a politeness setting

Stryker defaults to roughly one worker per core. Every worker runs the whole test
project in its own process, and these fixtures end with
`AppLifetime.KillAll("CalculatorApp")` — so six workers would each kill the other
five's applications. Tests would fail for reasons unrelated to the mutant, every
mutant would report **killed**, and the score would come out near 100% while
measuring nothing.

That is the same failure shape as the rest of this tool's traps: a plausible
number produced by a run that did not do what it claimed. Do not raise it without
first making the fixtures kill by process id and tolerate neighbours.

## `mutate` is scoped to what this suite is the right instrument for

Only the UIA-touching types, because the headless projects already cover the
rest — `UiaProperties` alone is 157 mutants that the unit tests kill. Mutating
everything here would multiply the slowest suite by mutants that a faster suite
already handles.

Paths are relative to the **mutated project** (`src/WindowsDriverCore.Automation`),
not to this directory. See `docs/STRYKER-NOTES.md`.

## `additional-timeout` is 60000

Integration tests launch applications and wait for real UI. The default budget is
tuned for unit tests, and anything slower is recorded as a `Timeout`, which is
indistinguishable from a survivor in the summary and quietly wrong in the score.

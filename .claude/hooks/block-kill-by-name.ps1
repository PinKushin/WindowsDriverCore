<#
    Blocks a test from ending an application by process NAME.

    THE RULE, set 2026-08-30 after it cascaded the local suite twice:

        primary   DELETE /session - the driver closes what the driver opened
        fallback  AppLifetime.KillProcess, for a subject the driver never owned
        never     killing by process name, at any point
        never     killing anything at the START of a test

    See docs/LOCAL-SUITE-IS-WRONG.md. KillAll matches every process of that
    name, so it ends the instance another fixture is sharing and the developer's
    own copy alongside it.

    FALSE POSITIVES ARE THE WHOLE DESIGN PROBLEM HERE. This repository discusses
    the rule constantly - in doc comments, in docs/*.md, in commit messages - and
    a naive content regex would block writing the very documentation that
    explains the rule. So:

      - only .cs files, and only under tests/
      - Support/AppLifetime.cs is exempt: the method still EXISTS there as the
        fallback, and its own doc comment describes it
      - comment lines are stripped before matching, which is where every
        legitimate mention lives
      - matches a CALL - `KillAll(` - not the bare word

    Exit 2 blocks the tool call and shows stderr to the agent.
#>

$ErrorActionPreference = 'Stop'

$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }

try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

# NOT $input - that is a PowerShell automatic variable (the pipeline
# enumerator), and assigning to it makes every later read of it wrong. Cost the
# first run of this hook's own tests: real calls sailed through while every
# false-positive case passed, which looks like a working hook.
$toolInput = $payload.tool_input
if (-not $toolInput) { exit 0 }

$path = [string]$toolInput.file_path
if (-not $path) { exit 0 }

$normalised = $path -replace '\\', '/'

# Only test code. Production may call whatever it needs; the rule is about tests.
if ($normalised -notmatch '(?i)/tests/') { exit 0 }
if ($normalised -notmatch '(?i)\.cs$') { exit 0 }

# The fallback itself lives here, and so does the doc comment describing it.
if ($normalised -match '(?i)/Support/AppLifetime\.cs$') { exit 0 }

# Edit writes new_string; Write writes content. Take whichever is present.
$text = $null
foreach ($field in 'new_string', 'content') {
    if ($toolInput.PSObject.Properties.Name -contains $field -and $toolInput.$field) {
        $text = [string]$toolInput.$field
        break
    }
}
if (-not $text) { exit 0 }

# Strip comments before matching. Every legitimate mention of the rule in this
# repository is in a doc comment, and blocking those would make the rule
# impossible to explain in the place it applies.
$offenders = @()
$inBlockComment = $false

foreach ($line in ($text -split "`r?`n")) {
    $trimmed = $line.Trim()

    if ($inBlockComment) {
        if ($trimmed -match '\*/') { $inBlockComment = $false }
        continue
    }

    if ($trimmed -match '^/\*') {
        if ($trimmed -notmatch '\*/') { $inBlockComment = $true }
        continue
    }

    if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('*')) { continue }

    # A call, not the word. `KillAll(` with an argument list.
    if ($trimmed -match 'KillAll\s*\(') { $offenders += $trimmed }
}

if ($offenders.Count -eq 0) { exit 0 }

$detail = ($offenders | Select-Object -First 3 | ForEach-Object { "    $_" }) -join "`n"

[Console]::Error.WriteLine(@"
BLOCKED: a test may not end an application by process NAME.

$detail

KillAll matches EVERY process of that name, so it ends the instance another
fixture is sharing and the developer's own copy alongside it. Measured
2026-08-30: this cascaded the local suite twice in one night.

Use instead, in order of preference:

  1. DELETE /session  - the driver closes what the driver opened. This is the
                        shipped teardown path and the one that has to be correct.
  2. AppLifetime.KillProcess(id) - only for a subject the driver never owned.

And never kill anything at the START of a test: that means something before it
left the machine wrong, and clearing it there hides the real fault. Use
AppLifetime.SkipIfAlreadyRunning instead.

See docs/LOCAL-SUITE-IS-WRONG.md.
"@)

exit 2

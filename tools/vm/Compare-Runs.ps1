<#
.SYNOPSIS
    Diffs two compatibility runs by test NAME, and refuses to compare anything
    that is not a full run.

.DESCRIPTION
    Written after an ad-hoc comparison reported 287/290 - above WinAppDriver's
    281 - by selecting a TRX with `Select -First 1` on a filename prefix and
    picking a FILTERED run's file (6 results) instead of the full run's (290) at
    the same commit. Twenty tests appeared to recover; they were absent. Two of
    them fail because Edge is not installed on the guest, which no code change
    can fix, and that impossibility is what exposed the error.

    So this selects by RESULT COUNT, never by name, and says so when it refuses.

    SUBTRACTING TWO SCORES IS NOT A COMPARISON. A run that loses two tests and
    gains two reads as "no change". Only the names say what moved, which is why
    this prints them and prints the count second.

    THE REFERENCE'S OWN FAILURES ARE NOT BACKLOG. Nine tests fail for
    WinAppDriver too - no Edge, no Store apps - so a failing list read as a
    to-do list spends real work on tests the reference cannot pass either. Pass
    -Reference to subtract them.

.PARAMETER Before
    Commit prefix of the earlier run.

.PARAMETER After
    Commit prefix of the later run.

.PARAMETER Reference
    Optional commit prefix of a WinAppDriver run, to separate our backlog from
    the environmental failures we share with it.

.EXAMPLE
    .\Compare-Runs.ps1 -Before 352357a -After bfa5638 -Reference 044b71c8
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Before,
    [Parameter(Mandatory = $true)][string] $After,
    [string] $Reference = '',
    [string] $VMName = 'Win10-Baseline',
    [string] $Root = 'F:\Hyper-V',

    # The suite's size. A TRX with any other count is a filtered run, a crashed
    # host, or a renamed fixture - all three of which have produced a plausible
    # wrong number in this project before.
    [int] $ExpectedTests = 290
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$lines = Get-Content -LiteralPath (Join-Path (Join-Path $Root $VMName) 'credential.txt')
$credential = [System.Management.Automation.PSCredential]::new(
    $lines[0], (ConvertTo-SecureString $lines[1] -AsPlainText -Force))

Invoke-Command -VMName $VMName -Credential $credential `
    -ArgumentList $Before, $After, $Reference, $ExpectedTests -ScriptBlock {
    param($before, $after, $reference, $expected)

    $dir = 'C:\baseline\WindowsDriverCore\TestResults'

    function FullRun($prefix) {
        $candidates = @(Get-ChildItem "$dir\run-$prefix*.trx" -ErrorAction SilentlyContinue)
        if ($candidates.Count -eq 0) { throw "No TRX at all for '$prefix'." }

        $full = @($candidates | Where-Object {
            @(([xml](Get-Content $_.FullName)).TestRun.Results.UnitTestResult).Count -eq $expected
        })

        if ($full.Count -eq 0) {
            $sizes = ($candidates | ForEach-Object {
                $n = @(([xml](Get-Content $_.FullName)).TestRun.Results.UnitTestResult).Count
                "$($_.Name)=$n"
            }) -join ', '
            throw "No FULL run for '$prefix' - found $sizes, expected $expected. A filtered run is not a smaller suite and its score means nothing."
        }

        # Newest, so a re-run at the same commit is the one compared.
        $full | Sort-Object LastWriteTime | Select-Object -Last 1
    }

    function Outcomes($file) {
        $x = [xml](Get-Content $file.FullName)
        $h = @{}
        foreach ($r in $x.TestRun.Results.UnitTestResult) { $h[$r.testName] = ($r.outcome -eq 'Passed') }
        $h
    }

    $bFile = FullRun $before
    $aFile = FullRun $after
    $b = Outcomes $bFile
    $a = Outcomes $aFile

    $bPass = @($b.Keys | Where-Object { $b[$_] }).Count
    $aPass = @($a.Keys | Where-Object { $a[$_] }).Count

    "before : $($bFile.Name)   $bPass/$expected"
    "after  : $($aFile.Name)   $aPass/$expected"
    ''

    $recovered = @($a.Keys | Where-Object { $a[$_] -and -not $b[$_] } | Sort-Object)
    $regressed = @($a.Keys | Where-Object { -not $a[$_] -and $b[$_] } | Sort-Object)

    "RECOVERED ($($recovered.Count)):"
    if ($recovered.Count -eq 0) { '    none' } else { $recovered | ForEach-Object { "    $_" } }
    ''
    "REGRESSED ($($regressed.Count)):"
    if ($regressed.Count -eq 0) { '    none' } else { $regressed | ForEach-Object { "    $_" } }

    if ($reference -ne '') {
        $rFile = FullRun $reference
        $r = Outcomes $rFile
        $shared = @($a.Keys | Where-Object { -not $a[$_] -and -not $r[$_] } | Sort-Object)
        $ours   = @($a.Keys | Where-Object { -not $a[$_] -and $r[$_] } | Sort-Object)
        $rPass  = @($r.Keys | Where-Object { $r[$_] }).Count
        ''
        "reference: $($rFile.Name)   $rPass/$expected"
        ''
        "SHARED WITH THE REFERENCE - environmental, not backlog ($($shared.Count)):"
        $shared | ForEach-Object { "    $_" }
        ''
        "OURS - the backlog, every one of which the reference passes ($($ours.Count)):"
        $ours | ForEach-Object { "    $_" }
        ''
        # Arithmetic check on the method itself, not decoration: if these
        # disagree the two runs are not comparable and nothing above is safe.
        "$aPass + $($ours.Count) = $($aPass + $ours.Count), reference $rPass" +
            $(if (($aPass + $ours.Count) -eq $rPass) { '  (closes)' } else { '  (DOES NOT CLOSE - runs not comparable)' })
    }
}

<#
.SYNOPSIS
    Runs WinAppDriver's own compatibility suite in the Windows 10 guest against a
    chosen driver, and reports the score.

.DESCRIPTION
    The suite is the scoreboard, so the things that make a run comparable are
    encoded here rather than retyped each time. Three of them were learned the
    hard way on 2026-08-10.

    RESETTING THE ALARM STORE IS NOT TIDINESS, AND IT IS A WORKAROUND FOR OUR
    OWN GAP. The suite's cleanup works: WinAppDriver runs the whole suite from a
    fresh store and leaves exactly the one default alarm. Under our driver ~162
    accumulate, because DeletePreviouslyCreatedAlarmEntry calls FindElementByXPath
    (status 19, unimplemented) and Mouse.ContextClick - /moveto then /click
    (status 9, command not recognized) - inside a catch { break }, so every
    failure is silent. Once XPath and the mouse routes land, delete this reset.
    With that many alarms the app disables ONLY "Add new alarm" - measured, with
    its siblings and the whole command bar still enabled. Nine tests need that button, and they are exactly the nine
    that separated a 133 run from every 124 run afterwards. A run without this
    reset does not measure the driver; it measures how much junk the previous
    runs left behind. The alarms live in the UWP settings hive,
    Settings\settings.dat, NOT in LocalState - clearing LocalState changes
    nothing, which was tried first.

    THE COMMIT IS PINNED AND VERIFIED. An earlier run measured an unidentified
    commit because 'git fetch' failed with "detected dubious ownership" and the
    error was piped to Out-Null; the trx was named run--120221.trx with an empty
    sha and nobody noticed until the numbers disagreed. This aborts instead.

    DOTNET_ROOT MUST BE SET. The published exe resolves its framework against
    C:\Program Files\dotnet, which holds only 5.0.7 in this guest. Without it the
    host exits immediately with "not possible to find any compatible framework
    version" and nothing ever listens on 4723 - which reads as a hung run.

    Everything runs through the guest agent queue, because PowerShell Direct
    lands in session 0 where there is no desktop and no UI test can pass.

.PARAMETER Commit
    The commit to measure. Pinned deliberately: comparing two runs means changing
    one thing.

.PARAMETER Driver
    Which driver answers on 4723 - this project, or WinAppDriver as the baseline.

    SWAPPING WINAPPDRIVER VERSIONS IS NOT A PLAIN REINSTALL. The 1.2.99 RC
    installs to C:\Program Files\Windows Application Driver, while 1.2.1 installs
    to C:\Program Files (x86)\... - so the path this script uses depends on which
    one is present. Worse, the RC registers itself twice and its LaunchConditions
    refuse a downgrade with "A newer version of Windows Application Driver is
    already installed", which surfaces only as msiexec exit 1603. Uninstall every
    registered product code first (one of them may answer 1605, "unknown
    product", which is fine), then install. Elevation is required: the guest
    agent's desktop session is not elevated, so an install queued through it also
    returns 1603, for an entirely different reason.

.EXAMPLE
    .\Invoke-CompatibilitySuite.ps1 -Commit 21a18dd
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Commit,
    [ValidateSet('WindowsDriverCore', 'WinAppDriver')][string] $Driver = 'WindowsDriverCore',
    [string] $VMName = "Win10-Baseline",
    [string] $Root = "F:\Hyper-V",
    [int] $TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$credentialPath = Join-Path (Join-Path $Root $VMName) "credential.txt"
$lines = Get-Content -LiteralPath $credentialPath
$credential = [System.Management.Automation.PSCredential]::new(
    $lines[0], (ConvertTo-SecureString $lines[1] -AsPlainText -Force))

$job = @"
`$ErrorActionPreference = 'Continue'
`$env:Path = 'C:\dotnet;C:\Program Files\Git\cmd;' + `$env:Path
`$env:DOTNET_ROOT = 'C:\dotnet'
`$env:DOTNET_NOLOGO = 'true'
`$vstest = 'C:\baseline\testplatform\tools\net462\Common7\IDE\Extensions\TestPlatform\vstest.console.exe'

Get-Process Time, Calculator, notepad, WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue |
    Stop-Process -Force
Start-Sleep -Seconds 4

# THE RESET IS AN INSTRUMENT, NOT JUST A CRUTCH.
#
# The suite is supposed to clean up after itself:
# AlarmClockBase.DeletePreviouslyCreatedAlarmEntry finds each alarm it made,
# right-clicks it and clicks Delete. If alarms are still here when a run starts,
# that cleanup FAILED, and it failed against this driver - which is our defect,
# not a fact about the suite.
#
# The mouse half is implemented (/moveto, /click with button 2). The suspected
# gap is the next line, FindElementByName("Delete"): a context menu opens in its
# own top-level window and our find is rooted at the session's window, so it
# would never see it. Suspected, not measured - the request transcript now
# records every find, so a run says which step actually failed.
#
# So the leftover count is REPORTED before the store is cleared. Zero means the
# suite cleaned up and the reset was a no-op. Non-zero is the defect, visible in
# every run's log instead of being quietly absorbed by the reset.
#
# The reset itself stays, because a CI runner genuinely starts clean and runs
# have to be independent of each other. What it must never do is hide that we
# are the reason it is needed.
`$pkg = "`$env:LOCALAPPDATA\Packages\Microsoft.WindowsAlarms_8wekyb3d8bbwe"
`$settings = Join-Path `$pkg 'Settings'

# Reported BEFORE the reset, or the evidence is destroyed by the thing being
# measured. settings.dat is the alarm store; its size tracks how much the suite
# left behind.
`$store = Join-Path `$settings 'settings.dat'
if (Test-Path `$store) {
    'alarm store left by the previous run : {0:N0} bytes  (a run that cleaned up leaves the baseline size)' -f (Get-Item `$store).Length
} else {
    'alarm store left by the previous run : none'
}
if (Test-Path `$settings) {
    try {
        Move-Item -LiteralPath `$settings -Destination "`$settings.reset-`$(Get-Date -Format yyyyMMdd-HHmmss)" -ErrorAction Stop
        'alarm store reset'
    } catch { 'alarm store NOT reset: ' + `$_.Exception.Message }
}
Get-ChildItem `$pkg -Directory -Filter 'Settings.reset-*' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -Skip 3 |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# NO WARM STEP. A COLD RUN IS THE SCORE.
#
# This used to pre-start Alarms & Clock and wait for AddAlarmButton to be
# enabled. It was added 2026-08-10 because resetting the alarm store closes the
# app, and every ActionsError_* test then failed in TestInit with "Currently
# selected window has been closed" - from the FIRST test of the run.
#
# THAT READING WAS WRONG, and the git record proves it. WinAppDriver scored
# 281/290 at 08-10 02:24 (b785879), SEVEN HOURS BEFORE the warm step existed
# (c62ebde, 09:38) - a cold run - and scored 281 again warm afterwards. It is
# warm-insensitive.
#
# So warming never was environmental control. Only THIS driver gained from it,
# which means it was compensating for our own cold-start defect and quietly
# flattering our numbers against a reference that did not need the help. A
# session created against a cold packaged application getting a window that dies
# is our defect. CI never warm-boots, so a warm score measures something no CI
# run will ever reproduce.
#
# The store RESET stays. That is contamination control rather than evasion: the
# suite fills the app to its cap, and a fresh CI runner starts clean anyway.
#
# What replaces the warm is its opposite - assert the subject is INSTALLED and
# NOT RUNNING, so the run is genuinely cold and a missing application fails
# loudly here rather than as 290 mysterious test failures.
'alarms installed : ' + (Get-AppxPackage Microsoft.WindowsAlarms).Version
'calc installed   : ' + (Get-AppxPackage Microsoft.WindowsCalculator).Version
Get-Process Time, Calculator, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
'cold start       : no subject application left running'

Set-Location 'C:\baseline\WindowsDriverCore'
& git.exe fetch origin 2>&1 | Out-String | Write-Host
& git.exe reset --hard $Commit 2>&1 | Out-String | Write-Host
`$head = (& git.exe rev-parse --short HEAD 2>&1 | Out-String).Trim()

# VALIDATED AS A SHA, not merely as non-empty.
#
# `$head is built from a command whose STDERR is merged in, so a git failure does
# not produce an empty string - it produces the error text. The old guard was
# `if (-not `$head)`, which that passes. Measured 2026-08-11: a "dubious
# ownership" failure put four lines of diagnostics into `$head, the run continued,
# the .trx filename became that text, vstest rejected the logger URI as invalid
# and ran WITH NO LOGGER. Zero results, exit code 0, and a log that looked like a
# completed run.
#
# The guard exists to refuse an UNIDENTIFIED commit. It also has to refuse a
# MISIDENTIFIED one.
if (`$head -notmatch '^[0-9a-f]{7,40}$') {
    'ABORT: HEAD is not a commit id, refusing to measure an unidentified commit.'
    'git said: ' + `$head
    exit 1
}

# AND IT HAS TO BE THE ONE THAT WAS ASKED FOR.
#
# The format check above is not enough. Measured 2026-08-11: a run requested
# 492484d, the guest's bundle did not contain it, `git reset --hard` failed, HEAD
# stayed at the previous commit - and the run labelled itself with THAT, passing
# the format check on the way through. A .trx named for a commit the run did not
# use is worse than an unlabelled one, because it looks like evidence.
#
# Harmless for a WinAppDriver run, which builds nothing. Silently wrong for one
# of ours.
if (-not '$Commit'.StartsWith(`$head) -and -not `$head.StartsWith('$Commit')) {
    'ABORT: asked for $Commit but HEAD is ' + `$head + '.'
    'The guest repository does not have that commit - refresh the bundle:'
    '  git bundle create <payload>\WindowsDriverCore.bundle --all'
    '  Copy-VMFile ... -DestinationPath C:\baseline\payload\WindowsDriverCore.bundle'
    exit 1
}
'head: ' + `$head

if ('$Driver' -eq 'WindowsDriverCore') {
    & C:\dotnet\dotnet.exe publish src\WindowsDriverCore.Host -c Release -o C:\baseline\host --nologo -v q 2>&1 |
        Select-String -Pattern 'WindowsDriverCore.Host ->|error' | ForEach-Object { `$_.Line }
    # THE TRANSCRIPT IS THE POINT OF RUNNING OURS.
    #
    # A count says 290 ran and N failed. This says WHICH request failed, with
    # which locator, at which step of which command, and what each one cost -
    # for all 290. Chasing the last set of failures took a bespoke probe per
    # question because the server said nothing about what it had answered.
    `$env:WINDOWSDRIVERCORE_LOG = 'C:\baseline\transcript-' + `$head + '-' + (Get-Date -Format HHmmss) + '.log'
    'transcript: ' + `$env:WINDOWSDRIVERCORE_LOG
    `$srv = Start-Process -FilePath 'C:\baseline\host\WindowsDriverCore.exe' -PassThru
} else {
    `$srv = Start-Process -FilePath 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe' -PassThru
}

`$up = `$false
for (`$i = 0; `$i -lt 40; `$i++) {
    try { `$null = Invoke-RestMethod -Uri 'http://127.0.0.1:4723/status' -TimeoutSec 5; `$up = `$true; break }
    catch { Start-Sleep -Seconds 1 }
}
'port 4723 answering: ' + `$up
if (-not `$up) { 'ABORT: driver never answered'; exit 1 }

`$trx = "run-`$head-$Driver-`$(Get-Date -Format HHmmss).trx"
'results: ' + `$trx
& `$vstest 'C:\baseline\WebDriverAPI\WebDriverAPI.dll' "/Logger:trx;LogFileName=`$trx" `
    /ResultsDirectory:C:\baseline\WindowsDriverCore\TestResults 2>&1 |
    Select-String -Pattern '^Total tests|^\s+Passed:|^\s+Failed:' | ForEach-Object { `$_.Line }

Stop-Process -Id `$srv.Id -Force -ErrorAction SilentlyContinue

# CLOSE THE APPLICATIONS TOO. The run kills these on the way IN and used to leave
# them running on the way OUT, so every run handed the next one a warm application
# it had not asked for - which is exactly the difference between a cold launch and
# a re-attach, and that difference has already been credited to code changes twice.
# Observed 2026-08-11: CalculatorApp still up 40 minutes after the suite finished.
Get-Process Time, Calculator, CalculatorApp, notepad, WinAppDriver, WindowsDriverCore ``
    -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# THE APP VERSION IS PART OF THE RESULT. The guest's Alarms & Clock updated itself
# on 2026-08-10 and took WinAppDriver's own score from 281/290 to 231/290, which
# made every comparison across that date wrong by 50 tests with nothing in any log
# to say so. A score without this line is not comparable to another score.
'alarms version: ' + (Get-AppxPackage Microsoft.WindowsAlarms).Version

# The nine that depend on "Add new alarm" being enabled. Reported explicitly
# because they move together and are the fastest signal that the reset worked.
`$nine = 'ClearElement','GetElementText','FindElements_ByName',
         'ClearElementError_StaleElement','ClickElementError_StaleElement',
         'GetElementAttributeError_StaleElement','GetElementDisplayedStateError_StaleElement',
         'GetElementSelectedStateError_StaleElement','GetElementTextError_StaleElement'
`$path = "C:\baseline\WindowsDriverCore\TestResults\`$trx"
if (Test-Path `$path) {
    [xml]`$x = Get-Content -LiteralPath `$path
    '=== the nine add-alarm tests ==='
    foreach (`$n in `$nine) {
        `$r = `$x.TestRun.Results.UnitTestResult | Where-Object { `$_.testName -eq `$n }
        '  {0,-44} {1}' -f `$n, `$(if (`$r) { `$r.outcome } else { 'ABSENT' })
    }
}
'run complete'
"@

$name = "compat-$(Get-Date -Format HHmmss)"
Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $job, $name -ScriptBlock {
    param($text, $jobName)
    [System.IO.File]::WriteAllText("C:\baseline\cmd\$jobName.ps1", $text)
}
Write-Host "queued $name ($Driver at $Commit)"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    $log = Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $name -ScriptBlock {
        param($jobName)
        if (Test-Path "C:\baseline\cmd\$jobName.done") { Get-Content "C:\baseline\cmd\$jobName.log" -Raw } else { $null }
    }
    if ($log) { Write-Host $log; return }
    Start-Sleep -Seconds 15
}
throw "The run did not finish within $TimeoutMinutes minutes."

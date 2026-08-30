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
    [int] $TimeoutMinutes = 30,

    # HOW MANY TIMES TO RUN THE SAME BINARY.
    #
    # The SendKeys cascade fires in roughly 30% of runs, so one run attributes
    # nothing and three consecutive green ones are the EXPECTED outcome whether
    # or not anything was fixed. Repeating is how that variance gets measured
    # rather than guessed at.
    #
    # Inside the script rather than a loop around it, for a reason that bit
    # twice: the HEAD guard re-checks the working tree on every invocation, so a
    # commit made while an external loop was running aborted the next run.
    # Publishing once and repeating only the queue removes that, and skips a
    # redundant publish and bundle push per run.
    [ValidateRange(1, 20)][int] $Repeat = 1,

    # OPT IN TO RESETTING. IT IS NOT THE DEFAULT, BECAUSE IT COSTS 21 TESTS.
    #
    # MEASURED 2026-08-11, WinAppDriver 1.2.1, same guest, same commit, cold:
    #
    #   with the reset     259 / 259 / 260     21 ActionsError failures
    #   without it         280                  0 ActionsError failures
    #
    # The reset moves the app's whole Settings folder away, so the next launch is
    # a genuine cold start - and a cold activation is where an intermediate or
    # short-lived process id comes back instead of the final one. Both drivers
    # then hunt for a window owned by a process that does not own it. Without the
    # reset the suite re-attaches to a running application whose pid is stable,
    # and the failure never happens.
    #
    # The reset and the warm step were added in the SAME commit (c62ebde). The
    # warm masked the damage the reset did, the two roughly cancelled, and the
    # pair survived a day of scrutiny because the net score looked fine. Removing
    # the warm exposed it; removing the reset fixes it.
    #
    # Keep the switch for a run that deliberately wants a pristine store, and
    # expect to lose those 21 tests when you use it.
    [switch] $ResetStore,

    # RUN A SUBSET, FOR AN EXPERIMENT RATHER THAN A SCORE.
    #
    # A full run is ~25 minutes, which makes "vary one thing and measure" too
    # expensive to do properly - and this repository has repeatedly reasoned its
    # way to a wrong answer rather than spend a run. A vstest /TestCaseFilter
    # turns that into well under a minute.
    #
    # TWO THINGS TO KNOW BEFORE TRUSTING A FILTERED RUN.
    #
    # A filter matching NOTHING exits 0 with no summary, so the caller sees
    # success. The no-match line is captured below for exactly that reason.
    #
    # A FILTERED RUN IS NOT A SMALLER VERSION OF THE SUITE. Tests share one
    # session per fixture and inherit each other's application state, so running
    # a few alone changes the conditions they run under. Use it to compare a
    # subset against ITSELF across a manipulation - never to claim a test would
    # pass in a full run, and never to quote a score.
    [string] $TestCaseFilter = '',

    # ENVIRONMENT FOR THE DRIVER PROCESS, as NAME=VALUE strings.
    #
    # The other half of making an experiment affordable: sweep a constant
    # without a rebuild per value. Set immediately before the driver starts, so
    # nothing between here and there can be blamed for the result.
    #
    # Anything set through this is by definition NOT the shipped configuration.
    # Record the value in whatever the experiment concludes, and never leave the
    # sweep hook in the product afterwards.
    [string[]] $DriverEnvironment = @()
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$credentialPath = Join-Path (Join-Path $Root $VMName) "credential.txt"
$lines = Get-Content -LiteralPath $credentialPath
$credential = [System.Management.Automation.PSCredential]::new(
    $lines[0], (ConvertTo-SecureString $lines[1] -AsPlainText -Force))

$driverEnvBlock = ($DriverEnvironment | ForEach-Object {
    $name, $value = $_ -split '=', 2
    if (-not $name -or $null -eq $value) { throw "DriverEnvironment needs NAME=VALUE, got '$_'" }
    # Echoed as well as set: a sweep whose value never reached the driver looks
    # exactly like a value that made no difference.
    "    `$env:$name = '$value'`n    'driver env: $name=$value'"
}) -join "`n"

$job = @"
`$ErrorActionPreference = 'Continue'
`$env:Path = 'C:\dotnet;C:\Program Files\Git\cmd;' + `$env:Path
`$env:DOTNET_ROOT = 'C:\dotnet'
`$env:DOTNET_NOLOGO = 'true'
`$vstest = 'C:\baseline\testplatform\tools\net462\Common7\IDE\Extensions\TestPlatform\vstest.console.exe'

# FILE EXPLORER WINDOWS, WHICH CANNOT BE KILLED BY PROCESS NAME.
#
# explorer.exe is the desktop shell AND every File Explorer window, so
# Stop-Process on it takes the taskbar and Start menu with it. The suite opens
# Explorer windows (root and window-handle tests) and closes none of them; a
# previous session found 22 accumulated on the guest, visible only from
# session 1.
#
# They are not inert. Leftover top-level windows sit in the z-order and change
# what is foreground and who owns a screen point - which this driver's click
# path consults directly.
#
# Shell.Application enumerates the windows rather than the process, so .Quit()
# closes each one and leaves the shell running. It has to run HERE, in the
# agent's session 1: the same call from session 0 sees nothing at all.
try {
    `$shell = New-Object -ComObject Shell.Application
    `$windows = @(`$shell.Windows())
    'explorer windows open: ' + `$windows.Count
    foreach (`$w in `$windows) { try { `$w.Quit() } catch { } }
    Start-Sleep -Seconds 2
    'explorer windows after: ' + @((New-Object -ComObject Shell.Application).Windows()).Count
} catch { 'could not enumerate explorer windows: ' + `$_.Exception.Message }

Get-Process Time, Calculator, notepad, WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue |
    Stop-Process -Force
Start-Sleep -Seconds 4

# THE RESET IS A CRUTCH WHOSE NECESSITY IS NOT ESTABLISHED.
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
# The suspected gap was FindElementByName("Delete") on a context menu, which opens
# in its own top-level window while our find is rooted at the session's window.
# That suspicion is now UNSUPPORTED: the owner checked the app after a run and the
# alarms were gone, so the whole chain - XPath find, right-click through /moveto
# and /click button 2, and the find on the context menu - works against this
# driver.
#
# Which leaves the reset without a demonstrated purpose beyond making runs
# independent of each other. That is a real purpose, but it is not the one it was
# introduced for.
#
# The reset itself stays, because a CI runner genuinely starts clean and runs
# have to be independent of each other. What it must never do is hide that we
# are the reason it is needed.
if ('$ResetStore' -ne 'True') {
    'alarm store    : NOT reset (the default - resetting costs 21 tests, see the comment above)'
} else {
    `$pkg = "`$env:LOCALAPPDATA\Packages\Microsoft.WindowsAlarms_8wekyb3d8bbwe"
    `$settings = Join-Path `$pkg 'Settings'
    
    # settings.dat is a REGISTRY HIVE, and its size is NOT an alarm count.
    #
    # Hives allocate in blocks and never shrink, and this one holds every setting
    # the app has, so a growth from 8,192 to 16,384 bytes means a block was
    # allocated at some point - nothing more. Written on 2026-08-11 as "its size
    # tracks how much the suite left behind" and quoted twice as evidence that our
    # cleanup was failing, until the owner looked in the app and reported the
    # alarms were gone. Measuring file size as a proxy for alarm count is the
    # wrong-instrument failure this project's own testing rules describe.
    #
    # A real count needs the app running, and launching it is exactly the warm
    # step that was removed. So there is no honest COLD measurement of leftovers
    # available, and the size is reported as what it is: a weak signal that the
    # store exists and roughly how big it has grown.
    `$store = Join-Path `$settings 'settings.dat'
    if (Test-Path `$store) {
        'settings.dat : {0:N0} bytes  (hive size, NOT an alarm count - see the comment above)' -f (Get-Item `$store).Length
    } else {
        'settings.dat : absent - the app has not run since the last reset'
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
}

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
    # BUILT ON THE HOST, NOT HERE. The guest has no network by design, so a
    # NuGet restore cannot run: measured 2026-08-11, every project failed with
    # NU1301 "No such host is known (api.nuget.org:443)" and the run aborted with
    # no driver. Staging the SDK was not enough - the package cache would have to
    # be staged too, and then kept in sync with every PackageReference forever.
    #
    # Publishing on the host removes the whole problem: the guest needs binaries,
    # not a build. What arrives is exactly what the requested commit produces.
    Expand-Archive -Path 'C:\baseline\payload\host.zip' -DestinationPath 'C:\baseline\host' -Force
    'host binaries: ' + (Get-ChildItem 'C:\baseline\host' -Filter 'WindowsDriverCore.exe').Length + ' bytes'
    # THE TRANSCRIPT IS THE POINT OF RUNNING OURS.
    #
    # A count says 290 ran and N failed. This says WHICH request failed, with
    # which locator, at which step of which command, and what each one cost.
    #
    # Added once already and deleted by a later edit to this same block, which
    # is why the first 163/290 run produced a number and no evidence. Set it
    # IMMEDIATELY before Start-Process so anything inserted above cannot
    # silently separate the two.
    `$env:WINDOWSDRIVERCORE_LOG = 'C:\baseline\transcript-' + `$head + '-' + (Get-Date -Format HHmmss) + '.log'
    'transcript: ' + `$env:WINDOWSDRIVERCORE_LOG
$driverEnvBlock
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
# A FILTER THAT MATCHES NOTHING EXITS 0 AND PRINTS NO SUMMARY, so the
# no-match line is captured too and reported rather than read as a pass.
# ONE ARRAY, SPLATTED ONCE. Splatting cannot appear on a backtick-continued
# line - PowerShell parses it as an expression there and refuses outright - so
# the whole argument list is built before the call.
`$vstestArgs = @(
    'C:\baseline\WebDriverAPI\WebDriverAPI.dll'
    "/Logger:trx;LogFileName=`$trx"
    '/ResultsDirectory:C:\baseline\WindowsDriverCore\TestResults'
)
if ('$TestCaseFilter' -ne '') { `$vstestArgs += '/TestCaseFilter:$TestCaseFilter' }

& `$vstest @vstestArgs 2>&1 |
    Select-String -Pattern '^Total tests|^\s+Passed:|^\s+Failed:|^No test (matches|is available)' |
    ForEach-Object { `$_.Line }

Stop-Process -Id `$srv.Id -Force -ErrorAction SilentlyContinue

# AND THE EXPLORER WINDOWS, WHICH THE SUITE LEAVES BEHIND EVERY TIME.
#
# Cleaned at the START of a run since bc2889f, which stopped runs inheriting
# each other's clutter but left the desktop covered afterwards - 6 or 7 windows
# survived the run that scored 182, matching the 8 explorer launches its
# transcript recorded.
#
# DELETE /session cannot fix this from inside the driver, and that is the real
# problem underneath. Our terminator ends the tracked PROCESS, and an Explorer
# window belongs to the long-running shell - killing that pid takes the desktop,
# taskbar and Start menu with it. So the driver correctly does nothing, and the
# windows accumulate. Closing a WINDOW rather than ending a process is a
# capability this driver does not have yet; see docs/LIMITATIONS.md.
try {
    `$open = @((New-Object -ComObject Shell.Application).Windows())
    foreach (`$w in `$open) { try { `$w.Quit() } catch { } }
    'explorer windows closed after the run: ' + `$open.Count
} catch { 'could not close explorer windows: ' + `$_.Exception.Message }

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

    # EVERY FAILING NAME, WITH ITS MESSAGE, PRINTED BY THE RUNNER ITSELF.
    #
    # Added 2026-08-30 after asking for these DURING a -Repeat cost a run. An
    # Invoke-Command against the guest while this script holds its own PSDirect
    # session kills the session with "The Hyper-V socket target process has
    # ended" - and the guest keeps going, so the run is not lost, only the
    # host's view of it. The obvious lesson is not to query mid-run; the better
    # one is to remove the reason to.
    #
    # The names are the durable part of a run. A score cannot be attributed to a
    # commit - the suite has a several-test band - and subtracting two scores
    # reads a run that lost two tests and gained two as "no change".
    #
    # The MESSAGE is here because a test name has lied about its own cause
    # twice: MouseDownMoveUp failing on the DIRECTION assertion rather than the
    # movement one is what unmasked MouseDoubleClick as the same defect.
    '=== every failure, by name ==='
    `$failed = @(`$x.TestRun.Results.UnitTestResult | Where-Object { `$_.outcome -ne 'Passed' })
    foreach (`$r in `$failed | Sort-Object testName) {
        `$message = (`$r.Output.ErrorInfo.Message -replace "\r?\n", ' ') -replace '\s+', ' '
        if (`$message.Length -gt 110) { `$message = `$message.Substring(0, 110) + '...' }
        '  {0,-46} {1}' -f `$r.testName, `$message
    }
}
'run complete'
"@

# ---------------------------------------------------------------------------
# Publish on the HOST when the driver under test is ours.
#
# The guest has no network, so it cannot restore packages. It also does not
# need to: it needs binaries. Publishing here means what runs on the guest is
# exactly what the requested commit produces, and it removes an entire class of
# guest-side build failure from the measurement path.
#
# The host tree must BE that commit. Building a different tree and labelling the
# result with $Commit is precisely the mislabelling the HEAD guard exists to
# stop, one machine over.
# ---------------------------------------------------------------------------
if ($Driver -eq 'WindowsDriverCore') {
    $hostHead = (& git rev-parse --short HEAD).Trim()
    if (-not $Commit.StartsWith($hostHead) -and -not $hostHead.StartsWith($Commit)) {
        throw "The host tree is at $hostHead but $Commit was requested. Check out $Commit first - publishing a different tree and labelling it $Commit is a fabricated result."
    }

    $dirty = (& git status --porcelain) | Where-Object { $_ }
    if ($dirty) {
        Write-Warning "The host tree has uncommitted changes. The published binaries will not match $hostHead exactly:"
        $dirty | ForEach-Object { Write-Warning "  $_" }
    }

    $publish = Join-Path $env:TEMP "wdc-publish-$hostHead"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host "Publishing $hostHead on the host..."
    & dotnet publish src/WindowsDriverCore.Host -c Release -o $publish --nologo -v q 2>&1 |
        Select-String -Pattern 'error|warning' | ForEach-Object { $_.Line }

    $exe = Join-Path $publish 'WindowsDriverCore.exe'
    if (-not (Test-Path $exe)) { throw "Publish produced no WindowsDriverCore.exe in $publish" }

    $zip = Join-Path $env:TEMP "wdc-host-$hostHead.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    'published {0:N1} MB' -f ((Get-Item $zip).Length / 1MB)

    Copy-VMFile -Name $VMName -SourcePath $zip -DestinationPath 'C:\baseline\payload\host.zip' -CreateFullPath -FileSource Host -Force
    Write-Host 'host binaries pushed to the guest'

    # THE BUNDLE GOES WITH THEM, EVERY TIME.
    #
    # The guest resets its clone to $Commit to get the SUITE-side scripts and the
    # test assemblies' source of truth, and it can only do that for a commit its
    # bundle contains. Refreshing that bundle used to be a manual step named only
    # in the abort message - so the first run after any new commit aborted, and
    # before the exit-code fix below it aborted while reporting success.
    #
    # --all rather than the branch, so a run can measure any commit reachable
    # from any ref rather than only the current branch's tip.
    $bundle = Join-Path $env:TEMP "wdc-$hostHead.bundle"
    if (Test-Path $bundle) { Remove-Item $bundle -Force }

    & git bundle create $bundle --all 2>&1 | Out-Null

    if (-not (Test-Path $bundle)) { throw "git bundle produced nothing at $bundle" }

    'bundle {0:N1} MB' -f ((Get-Item $bundle).Length / 1MB)

    Copy-VMFile -Name $VMName -SourcePath $bundle `
        -DestinationPath 'C:\baseline\payload\WindowsDriverCore.bundle' `
        -CreateFullPath -FileSource Host -Force
    Write-Host 'bundle pushed to the guest'
}

for ($attempt = 1; $attempt -le $Repeat; $attempt++) {

if ($Repeat -gt 1) { Write-Host "--- run $attempt of $Repeat at $Commit ---" }

$name = "compat-$(Get-Date -Format HHmmss)"
Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $job, $name -ScriptBlock {
    param($text, $jobName)
    [System.IO.File]::WriteAllText("C:\baseline\cmd\$jobName.ps1", $text)
}
Write-Host "queued $name ($Driver at $Commit)"

$finished = $false
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    $log = Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $name -ScriptBlock {
        param($jobName)
        if (Test-Path "C:\baseline\cmd\$jobName.done") { Get-Content "C:\baseline\cmd\$jobName.log" -Raw } else { $null }
    }
    if ($log) {
        Write-Host $log

        # AN ABORT INSIDE THE GUEST MUST NOT EXIT 0 OUT HERE.
        #
        # The guest script exits 1 on its own aborts, but that exit code dies
        # with the guest process - the host only ever sees the LOG. So a run
        # that never started ("the guest repository does not have that commit")
        # printed its abort and this script returned success, which is the exact
        # defect the whole audit has been about: reporting work that did not
        # happen. Measured 2026-08-29, on a run whose bundle was stale.
        #
        # Matched on the log rather than plumbed through a status file, because
        # the log is what every abort already writes and a second channel is a
        # second thing to keep in sync.
        if ($log -match '(?m)^ABORT:') {
            throw "The guest aborted the run. See the log above."
        }

        $finished = $true
        break
    }
    Start-Sleep -Seconds 15
}
if (-not $finished) { throw "Run $attempt did not finish within $TimeoutMinutes minutes." }
}

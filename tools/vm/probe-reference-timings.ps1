# What does WinAppDriver actually spend, so our budgets can match it?
#
# THE CONTRACT (owner, 2026-08-12): succeed as early as possible, but give up no
# sooner than WinAppDriver would. A client that gets "no such element" faster
# than the reference would have answered receives a DIFFERENT result, not merely
# a quicker one - the application had not rendered yet - and the compatibility
# suite will not catch it on a fast machine.
#
# So a budget must be measured from the reference, never inferred from our own
# timings. ContentReadyLauncher's 500 ms cap was set at roughly eight times OUR
# measured content gap, which is exactly the reasoning this probe exists to
# replace: at 6396a8a one AppName lookup still missed, and a cap chosen this way
# cannot say whether that is too short.
#
# Three numbers per driver, several rounds each:
#
#   session      POST /session, launch to answer
#   find hit     a find for an element that IS there
#   find miss    a find for an element that is NOT there, implicit wait 0
#                -> this is the GIVE-UP time, the one we must not undercut
#
# The suite is never used for this: it measures pass and fail, not latency, and
# it is kept pristine. Raw HTTP against both drivers, same operations, same guest.
#
# NOTE: the driver loop is called MeasureDriver, not Measure. "Measure" is the
# built-in alias for Measure-Object, and PowerShell resolves the alias first -
# the first version of this probe failed with "A positional parameter cannot be
# found that accepts argument 'C:\Program Files (x86)\...'", which is
# Measure-Object complaining about an executable path.
#
# NOTE: on Windows 10 the Calculator process is named "Calculator" and Alarms is
# "Time". An earlier probe cleaned up with the wrong name and left one running.

$ErrorActionPreference = 'Continue'
$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

function TimeIt([scriptblock] $work) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try { & $work | Out-Null } catch { }
    $sw.Stop()
    [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
}

function MeasureDriver($driverName, $exePath) {
    "=========================================================="
    "  $driverName"
    "=========================================================="

    Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process Calculator, Time -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    $srv = Start-Process -FilePath $exePath -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    $sessions = @()
    $hits = @()
    $misses = @()

    for ($round = 1; $round -le 4; $round++) {
        Get-Process Calculator -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 3

        $session = $null
        $ms = TimeIt { $script:session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId }
        $session = $script:session
        $sessions += $ms

        if (-not $session) { "  round {0}: no session" -f $round; continue }

        # Implicit wait 0, so a miss reports the driver's own give-up time rather
        # than the client's patience.
        try { PostRaw "/session/$session/timeouts" '{"type":"implicit","ms":0}' | Out-Null } catch { }

        $hits += TimeIt {
            PostRaw "/session/$session/element" '{"using":"accessibility id","value":"num5Button"}'
        }

        $misses += TimeIt {
            PostRaw "/session/$session/element" '{"using":"accessibility id","value":"NoSuchThingAnywhere"}'
        }

        "  round {0}: session {1,7} ms | find hit {2,6} ms | find miss {3,6} ms" -f `
            $round, $sessions[-1], $hits[-1], $misses[-1]

        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
    }

    function ReportStat($label, $values) {
        if ($values.Count -eq 0) { "  {0,-12} no data" -f $label; return }
        $min = ($values | Measure-Object -Minimum).Minimum
        $max = ($values | Measure-Object -Maximum).Maximum
        $avg = [math]::Round(($values | Measure-Object -Average).Average, 1)
        "  {0,-12} min {1,7} | avg {2,7} | max {3,7}" -f $label, $min, $avg, $max
    }

    ""
    ReportStat 'session' $sessions
    ReportStat 'find hit' $hits
    ReportStat 'find miss' $misses
    ""

    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process Calculator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

MeasureDriver 'WinAppDriver 1.2.1 (the reference)' 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
MeasureDriver 'WindowsDriverCore (ours)'           'C:\baseline\host\WindowsDriverCore.exe'

"Read 'session' first: that is how long the reference spends before answering,"
"and our content-ready wait must not give up sooner than the difference between"
"it and our own launch time. 'find miss' is the give-up budget for a find."
'=== probe complete ==='

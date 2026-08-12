# After a session closes its window, does the driver report NoSuchWindow?
#
# Utility.InitializeOrphanedSession creates a Calculator session, records the
# handle, and closes the window. Every *Error_NoSuchWindow test then expects a
# command against that session to answer
# "Currently selected window has been closed" (JWP status 23).
#
# On the 084fcac run four of those regressed and answered "An element could not
# be located..." (status 7) instead, meaning the window was still reported ALIVE
# after the close. That run is also the first where Launch_* passed, and those
# tests deliberately relaunch applications to add windows. Calculator is
# SINGLE-INSTANCE - a second window without a second process - so extra windows
# are exactly the condition that could change what closing one does.
#
# CASE 1 is the plain sequence. CASE 2 adds a second window first. If case 1
# answers 23 and case 2 answers 7, the extra window is the cause; if both answer
# 7, the close itself is at fault and Launch_* is innocent.

$ErrorActionPreference = 'Continue'
$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 30
}

function NewCalcSession {
    (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
}

# Returns "status=N message=..." for a find, which is the observation that matters.
function FindOutcome($session) {
    try {
        $r = PostRaw "/session/$session/element" '{"using":"accessibility id","value":"num5Button"}'
        "status=$($r.status) (FOUND an element)"
    }
    catch {
        $body = $null
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            $body = (New-Object System.IO.StreamReader($stream)).ReadToEnd() | ConvertFrom-Json
        } catch { }
        if ($body) { "status=$($body.status) message='$($body.value.message)'" }
        else { "no body: $($_.Exception.Message)" }
    }
}

function Probe($driverName, $exePath) {
    "=========================================================="
    "  $driverName"
    "=========================================================="

    Get-Process WinAppDriver, WindowsDriverCore, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2

    $srv = Start-Process -FilePath $exePath -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    try {
        # ---------------- CASE 1: one window, close it ----------------
        "--- CASE 1: single Calculator window ---"
        $a = NewCalcSession
        Start-Sleep -Seconds 2
        $handlesBefore = (Invoke-RestMethod -Uri "$base/session/$a/window_handles" -TimeoutSec 15).value
        "  window_handles before close : $($handlesBefore -join ', ')"

        Invoke-RestMethod -Method Delete -Uri "$base/session/$a/window" -TimeoutSec 20 | Out-Null
        Start-Sleep -Milliseconds 500

        $handlesAfter = (Invoke-RestMethod -Uri "$base/session/$a/window_handles" -TimeoutSec 15).value
        "  window_handles after close  : $(if ($handlesAfter) { $handlesAfter -join ', ' } else { '(empty)' })"
        "  find after close            : $(FindOutcome $a)"
        "  EXPECTED                    : status=23 'Currently selected window has been closed'"

        Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2

        # ---------------- CASE 2: a second window exists ----------------
        "--- CASE 2: a second Calculator window is open ---"
        $b = NewCalcSession
        Start-Sleep -Seconds 2
        $c = NewCalcSession          # single-instance: a window without a process
        Start-Sleep -Seconds 2

        $bHandles = (Invoke-RestMethod -Uri "$base/session/$b/window_handles" -TimeoutSec 15).value
        $cHandles = (Invoke-RestMethod -Uri "$base/session/$c/window_handles" -TimeoutSec 15).value
        "  session B handles           : $($bHandles -join ', ')"
        "  session C handles           : $($cHandles -join ', ')"
        "  same window?                : $(if (($bHandles -join ',') -eq ($cHandles -join ',')) { 'YES - the second session reused it' } else { 'no, distinct windows' })"

        Invoke-RestMethod -Method Delete -Uri "$base/session/$b/window" -TimeoutSec 20 | Out-Null
        Start-Sleep -Milliseconds 500

        "  find on B after its close   : $(FindOutcome $b)"
        "  EXPECTED                    : status=23"

        foreach ($s in @($b, $c)) {
            try { Invoke-RestMethod -Method Delete -Uri "$base/session/$s" -TimeoutSec 15 | Out-Null } catch { }
        }
    }
    finally {
        Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
        Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

Probe 'WinAppDriver 1.2.1 (the reference)' 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
Probe 'WindowsDriverCore (ours)'           'C:\baseline\host\WindowsDriverCore.exe'

'=== probe complete ==='

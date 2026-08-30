<#
    Does Calculator come back MAXIMIZED, and can we restore it?

    THE OWNER'S HYPOTHESIS, 2026-08-30:

        "if the maximise tests are failing it might be because the calculator
         isnt being restored to the smaller window between tests, its opening
         as maximized"

    TWO OBSERVATIONS ALREADY SUPPORT IT, both accidental:
      - probe-double-click-on-the-caption found Calculator already maximized on
        its first run, and its verdict column - written for a restored start -
        printed the exact opposite of its own raw values because of it.
      - probe-does-touch-need-the-raise's FIRST case could not restore the
        window: clicking the Maximize button did nothing for 3 seconds, and the
        probe had to fall back to the touch route. The other 7 cases restored in
        250 ms.

    WHY IT WOULD MATTER. Calculator is a packaged single-instance app that
    remembers its window state, so once anything maximizes it every later session
    starts maximized. Both maximize tests then depend on the SUITE'S OWN GUARD to
    put it back:

        if (!maximizeButton.Text.Contains("Maximize")) { maximizeButton.Click(); }
        Assert.IsTrue(maximizeButton.Text.Contains("Maximize"));

    which is an element click on the Restore button, served by our click ladder.
    If that click is unreliable, several Calculator tests inherit the flake.

    WHAT THIS MEASURES, and it separates the two halves:

      1. does a FRESH session inherit a maximized window?
         maximize, end the session, start a new one, report the state
      2. how reliably does the BUTTON CLICK restore it?
         ten attempts, each timed, reporting successes and the times

    The second is the one with a number in it. "It usually works" is what a
    flaky defect looks like from the inside, so this counts rather than judges.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$driver = 'C:\baseline\host\WindowsDriverCore.exe'
if (-not (Test-Path $driver)) { "ABORT: no driver at $driver"; return }

Get-Process WindowsDriverCore, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$server = Start-Process -FilePath $driver -PassThru -WindowStyle Minimized

function Wire([string] $method, [string] $path, [string] $body) {
    $a = @{ Uri = "http://127.0.0.1:4723$path"; Method = $method; TimeoutSec = 30; UseBasicParsing = $true }
    if ($method -ne 'GET') { $a.Body = $body; $a.ContentType = 'application/json' }
    try { (Invoke-WebRequest @a).Content }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return $null }
        $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
        try { $sr.ReadToEnd() } finally { $sr.Dispose() }
    }
}

function Value([string] $json) { if ($json) { ($json | ConvertFrom-Json).value } else { $null } }

function Find([string] $session, [string] $value) {
    (Value (Wire 'POST' "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$value`"}")).ELEMENT
}

function NewSession {
    (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
}

# 'Maximize Calculator' means the window is RESTORED; 'Restore Calculator' means
# it is maximized. Reported as the label so a reader can check the reasoning.
function StateOf([string] $session, [string] $maxId) {
    $text = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
    if (-not $text) { return 'unreadable' }
    if ("$text".Contains('Maximize')) { 'restored' } else { 'MAXIMIZED' }
}

try {
    $ready = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $ready = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $ready) { 'ABORT: driver never answered /status'; return }

    # ---- 1. does a fresh session inherit a maximized window? ----------------
    'PART 1 - does a new session inherit the maximized state?'

    $session = NewSession
    if (-not $session) { 'ABORT: no session'; return }
    Start-Sleep -Seconds 2

    $maxId = Find $session 'Maximize'
    $titleId = Find $session 'AppName'
    if (-not $maxId -or -not $titleId) { 'ABORT: no Maximize/AppName'; return }

    "  first launch of the run          : $(StateOf $session $maxId)"

    # Maximize it deliberately, by the route measured to work.
    if ((StateOf $session $maxId) -eq 'restored') {
        Wire 'POST' "/session/$session/touch/doubleclick" "{`"element`":`"$titleId`"}" | Out-Null
        Start-Sleep -Seconds 2
    }
    "  after deliberately maximizing    : $(StateOf $session $maxId)"

    Wire 'DELETE' "/session/$session" '{}' | Out-Null
    Start-Sleep -Seconds 2

    $session = NewSession
    if (-not $session) { 'ABORT: no second session'; return }
    Start-Sleep -Seconds 3
    $maxId = Find $session 'Maximize'
    $titleId = Find $session 'AppName'
    if (-not $maxId) { 'ABORT: no Maximize in the second session'; return }

    "  a NEW session then sees          : $(StateOf $session $maxId)"

    # ---- 2. how reliably does the button restore it? -----------------------
    ''
    'PART 2 - ten attempts to restore by clicking the button, as the suite does'

    $ok = 0
    $times = @()

    foreach ($attempt in 1..10) {
        # Ensure it is maximized before each attempt, using the route rather
        # than the thing under test.
        if ((StateOf $session $maxId) -eq 'restored') {
            Wire 'POST' "/session/$session/touch/doubleclick" "{`"element`":`"$titleId`"}" | Out-Null
            Start-Sleep -Seconds 2
        }

        if ((StateOf $session $maxId) -ne 'MAXIMIZED') {
            "  attempt $attempt : could not maximize first; skipped"
            continue
        }

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        Wire 'POST' "/session/$session/element/$maxId/click" '{}' | Out-Null

        $restored = $false
        while ($watch.Elapsed.TotalMilliseconds -lt 3000) {
            if ((StateOf $session $maxId) -eq 'restored') { $restored = $true; break }
            Start-Sleep -Milliseconds 100
        }

        if ($restored) { $ok++; $times += [int]$watch.Elapsed.TotalMilliseconds }
        "  attempt {0,2} : {1}" -f $attempt, $(if ($restored) { "restored in $([int]$watch.Elapsed.TotalMilliseconds) ms" } else { 'DID NOT RESTORE within 3000 ms' })
    }

    ''
    "  restored $ok of 10"
    if ($times.Count -gt 0) {
        "  times: $($times -join ', ') ms"
    }

    Wire 'DELETE' "/session/$session" '{}' | Out-Null
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
}

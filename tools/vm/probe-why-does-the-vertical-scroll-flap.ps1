<#
    Touch_Scroll_Vertical fails 4 runs in 8, Pen_Scroll_Vertical 3 in 8.

    THEY ARE THE WORST FLAPPERS IN THE BACKLOG, and flake is failure - the
    variance is the evidence, not the excuse. One run cannot tell a race inside
    the gesture from contamination by whatever ran before it, because both
    produce "sometimes".

    WHAT IS ALREADY KNOWN, and what it rules out:
      - The duration IS honoured. Measured from the run transcript: 7 /actions
        requests over 400 ms (514, 514, 549, 559, 1011, 1011, 3038), matching the
        tests' 500 ms and 1 s moves. So the gesture is not being executed
        instantly, and 200 px over ~514 ms is ~390 px/s.
      - The horizontal counterparts are VACUOUS on this app version - an empty
        `if` branch - so "horizontal works, vertical does not" was never a
        comparison. There is no axis-shaped clue.
      - Frames are 8 ms apart, so a 500 ms move is ~62 samples. That is a
        digitiser-like signal, not 10 Hz.

    THE EXPERIMENT: run the suite's own gesture TEN TIMES in one session and
    report each outcome, so a rate replaces an anecdote.

      down  = move to the selector, contact down, move to selector+(0,-200)
              over 500 ms, contact up          -> "00" must become HIDDEN
      up    = the same with +200               -> "00" must become SHOWN again

    Both directions are reported separately every round, because the recorded
    claim is that they are asymmetric and that has never been measured over
    more than a couple of runs.

    THE STATE IS RE-READ, NOT ASSUMED. `Displayed` is read before and after each
    gesture, so a round that starts in the wrong state is visible rather than
    silently scored as a failure - which is how a contaminated start would
    masquerade as a broken gesture.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$driver = 'C:\baseline\host\WindowsDriverCore.exe'
if (-not (Test-Path $driver)) { "ABORT: no driver at $driver"; return }

Get-Process WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
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

function ById([string] $session, [string] $id) {
    (Value (Wire 'POST' "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}")).ELEMENT
}

function ByName([string] $session, [string] $name) {
    (Value (Wire 'POST' "/session/$session/element" "{`"using`":`"name`",`"value`":`"$name`"}")).ELEMENT
}

function IsDisplayed([string] $session, [string] $element) {
    $v = Value (Wire 'GET' "/session/$session/element/$element/displayed" '{}')
    if ($null -eq $v) { 'unreadable' } elseif ($v) { 'shown' } else { 'hidden' }
}

# The suite's own sequence, with the element as the move ORIGIN. Selenium 3.8
# keys the origin with the JSON Wire ELEMENT name even inside a W3C /actions
# body, which is the per-command dialect split this project has measured.
function Scroll([string] $session, [string] $selector, [int] $dy) {
    $body = @"
{"actions":[{"type":"pointer","id":"finger","parameters":{"pointerType":"touch"},"actions":[
 {"type":"pointerMove","duration":0,"origin":{"ELEMENT":"$selector"},"x":0,"y":0},
 {"type":"pointerDown","button":0},
 {"type":"pointerMove","duration":500,"origin":{"ELEMENT":"$selector"},"x":0,"y":$dy},
 {"type":"pointerUp","button":0}
]}]}
"@
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $answer = Wire 'POST' "/session/$session/actions" $body
    [PSCustomObject]@{
        Cost = [int]$watch.Elapsed.TotalMilliseconds
        Status = if ($answer) { (($answer | ConvertFrom-Json).status) } else { 'no answer' }
    }
}

try {
    $ready = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $ready = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $ready) { 'ABORT: driver never answered /status'; return }

    $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
    if (-not $session) { 'ABORT: no Alarms session'; return }
    Start-Sleep -Seconds 3

    # THE PAGE IS DETECTED, NOT ASSUMED. Alarms & Clock reopens on whatever page
    # it was left on, and the previous run of this probe left it on Add Alarm -
    # where there is no AddAlarmButton, so the first version aborted claiming the
    # alarm list was at its cap. That diagnosis would have been wrong, and it is
    # the kind of wrong that sends someone to reset a store for no reason.
    $selector = ById $session 'MinuteLoopingSelector'

    if (-not $selector) {
        $add = ById $session 'AddAlarmButton'
        if (-not $add) {
            'ABORT: neither MinuteLoopingSelector nor AddAlarmButton - the list may genuinely be at its cap'
            return
        }
        Wire 'POST' "/session/$session/element/$add/click" '{}' | Out-Null
        Start-Sleep -Seconds 2

        $picker = ById $session 'AlarmTimePicker'
        if ($picker) { Wire 'POST' "/session/$session/element/$picker/click" '{}' | Out-Null; Start-Sleep -Seconds 1 }

        $selector = ById $session 'MinuteLoopingSelector'
    }

    if (-not $selector) { 'ABORT: no MinuteLoopingSelector'; return }

    # THE DISPLACEMENT, NOT "is 00 visible". A LoopingSelector WRAPS - measured
    # in the first version of this probe, where a DOWN scroll twice made '00'
    # appear - so visibility is a coarse proxy for position and cannot say how
    # far the gesture moved. The selector's own value is a number of items.
    #
    # The suite's test only works if down and up move the SAME distance, since it
    # asserts the selector returns to a state where '00' is visible again. So the
    # quantity that matters is |down| - |up|, and it is reported per round.
    "{0,-6} {1,-9} {2,-9} {3,-9} {4,-10} {5}" -f 'round', 'start', 'after dn', 'after up', 'net drift', 'costs'
    "{0,-6} {1,-9} {2,-9} {3,-9} {4,-10} {5}" -f '-----', '--------', '--------', '--------', '---------', '-----'

    $drifts = @()

    foreach ($round in 1..10) {
        $start = Value (Wire 'GET' "/session/$session/element/$selector/text" '{}')

        $d = Scroll $session $selector -200
        Start-Sleep -Seconds 1
        $afterDown = Value (Wire 'GET' "/session/$session/element/$selector/text" '{}')

        $u = Scroll $session $selector 200
        Start-Sleep -Seconds 1
        $afterUp = Value (Wire 'GET' "/session/$session/element/$selector/text" '{}')

        # Minutes are 0-59 and the selector loops, so the shortest signed
        # distance is the honest one.
        $drift = 'n/a'
        if ($start -match '^\d+$' -and $afterUp -match '^\d+$') {
            $raw = [int]$afterUp - [int]$start
            if ($raw -gt 30) { $raw -= 60 } elseif ($raw -lt -30) { $raw += 60 }
            $drift = $raw
            $drifts += $raw
        }

        "{0,-6} {1,-9} {2,-9} {3,-9} {4,-10} {5}" -f `
            $round, $start, $afterDown, $afterUp, $drift, "$($d.Cost)/$($u.Cost) ms"
    }

    ''
    if ($drifts.Count -gt 0) {
        $exact = @($drifts | Where-Object { $_ -eq 0 }).Count
        "  rounds where down and up cancelled exactly : $exact of $($drifts.Count)"
        "  net drift per round                        : $($drifts -join ', ') items"
    }

    Wire 'DELETE' "/session/$session" '{}' | Out-Null
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
}

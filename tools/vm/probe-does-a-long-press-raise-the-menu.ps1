<#
    Does an injected long press raise the alarm's context menu?

    TouchLongTap fails with "An element could not be located". The only
    FindElement in it that THROWS is the one after the gesture:

        touchScreen.LongPress(alarmEntries[0].Coordinates)
        Thread.Sleep(3s)
        session.FindElementByName("Delete").Click()   <- throws

    So the long press does not raise the menu. The menu itself is reachable: the
    suite's own DeletePreviouslyCreatedAlarmEntry opens it with a RIGHT-CLICK and
    that works - measured, and recorded in LIMITATIONS after the opposite was
    claimed twice.

    /touch/longclick holds a contact for 1000 ms with 16 ms update frames, which
    is WinAppDriver's own duration. So "we hold too briefly" is not the obvious
    answer, and the real question is whether injected touch participates in
    press-and-hold recognition at all.

    THE REFERENCE ANSWERS IT DIRECTLY, since it passes this test. Both drivers
    run the same gesture against the same subject in one script.

    FOUR CASES PER DRIVER, and two of them are controls:

      right-click            POSITIVE CONTROL - the menu must appear, or this
                             probe cannot detect a menu and every "no" below is
                             meaningless
      /touch/longclick       the gesture under test
      a short tap            NEGATIVE CONTROL - the menu must NOT appear, or
                             "a menu appeared" says nothing about the hold
      hold via /actions      2000 ms, to separate "too brief" from "not
                             recognised at all"  (this driver only - the
                             reference's /actions is a different implementation)

    It creates one alarm and deletes it by right-click at the end.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ours = 'C:\baseline\host\WindowsDriverCore.exe'
$reference = 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
$alarm = 'ProbeHold'

if (-not (Test-Path $ours)) { "ABORT: no driver at $ours"; return }
if (-not (Test-Path $reference)) { "ABORT: no WinAppDriver at $reference"; return }

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

function TheAlarmEntry([string] $session) {
    $xpath = "//ListItem[starts-with(@Name, `\`"$alarm`\`")]"
    (Value (Wire 'POST' "/session/$session/element" "{`"using`":`"xpath`",`"value`":`"$xpath`"}")).ELEMENT
}

# IS THE MENU UP? The suite's own question: a menu item named Delete.
function DeleteIsOffered([string] $session) { [bool] (ByName $session 'Delete') }

function DismissAnyMenu([string] $session) {
    # Escape closes a menu without choosing anything from it.
    Wire 'POST' "/session/$session/keys" '{"value":["\uE00C"]}' | Out-Null
    Start-Sleep -Milliseconds 600
}

function Measure([string] $label, [string] $exe, [bool] $withActions) {
    Get-Process WindowsDriverCore, WinAppDriver -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600

    $server = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
    try {
        $up = $false
        foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $up = $true; break }; Start-Sleep -Seconds 1 }
        if (-not $up) { "ABORT: $label never answered /status"; return }

        ''
        "=== $label ==="

        $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
        if (-not $session) { "ABORT: $label made no Alarms session"; return }
        Start-Sleep -Seconds 3

        # One alarm to hold on, created the way the suite creates them.
        if (-not (TheAlarmEntry $session)) {
            $add = ById $session 'AddAlarmButton'
            if (-not $add) { "ABORT: no AddAlarmButton"; Wire 'DELETE' "/session/$session" '{}' | Out-Null; return }
            Wire 'POST' "/session/$session/element/$add/click" '{}' | Out-Null
            Start-Sleep -Seconds 1

            $box = ById $session 'AlarmNameTextBox'
            if (-not $box) { "ABORT: no AlarmNameTextBox"; Wire 'DELETE' "/session/$session" '{}' | Out-Null; return }
            Wire 'POST' "/session/$session/element/$box/clear" '{}' | Out-Null
            Wire 'POST' "/session/$session/element/$box/value" "{`"value`":[`"$alarm`"]}" | Out-Null

            $save = (ById $session 'AlarmSaveButton')
            if (-not $save) { $save = ById $session 'PrimaryButton' }
            if (-not $save) { "ABORT: no save button"; Wire 'DELETE' "/session/$session" '{}' | Out-Null; return }
            Wire 'POST' "/session/$session/element/$save/click" '{}' | Out-Null
            Start-Sleep -Seconds 3
        }

        $entry = TheAlarmEntry $session
        if (-not $entry) { "ABORT: the alarm was not created, so there is nothing to hold"; Wire 'DELETE' "/session/$session" '{}' | Out-Null; return }

        $cases = @('right-click (POSITIVE)', '/touch/longclick', 'a short tap (NEGATIVE)')
        if ($withActions) { $cases += 'hold 2000 via /actions' }

        foreach ($case in $cases) {
            $entry = TheAlarmEntry $session
            if (-not $entry) { "  $case -> the alarm entry vanished"; continue }

            $loc = Value (Wire 'GET' "/session/$session/element/$entry/location" '{}')
            $size = Value (Wire 'GET' "/session/$session/element/$entry/size" '{}')
            $x = [int]$loc.x + [int]([int]$size.width / 2)
            $y = [int]$loc.y + [int]([int]$size.height / 2)

            switch -Wildcard ($case) {
                'right-click*' {
                    Wire 'POST' "/session/$session/moveto" "{`"element`":`"$entry`"}" | Out-Null
                    Wire 'POST' "/session/$session/click" '{"button":2}' | Out-Null
                }
                '/touch/longclick' {
                    Wire 'POST' "/session/$session/touch/longclick" "{`"element`":`"$entry`"}" | Out-Null
                }
                'a short tap*' {
                    Wire 'POST' "/session/$session/touch/click" "{`"element`":`"$entry`"}" | Out-Null
                }
                default {
                    $body = @"
{"actions":[{"type":"pointer","id":"finger","parameters":{"pointerType":"touch"},"actions":[
 {"type":"pointerMove","duration":0,"x":$x,"y":$y},
 {"type":"pointerDown","button":0},
 {"type":"pause","duration":2000},
 {"type":"pointerUp","button":0}
]}]}
"@
                    Wire 'POST' "/session/$session/actions" $body | Out-Null
                }
            }

            Start-Sleep -Seconds 3
            $offered = DeleteIsOffered $session
            "  {0,-26} menu: {1}" -f $case, $(if ($offered) { 'YES' } else { 'no' })
            if ($offered) { DismissAnyMenu $session }
        }

        # Clean up the alarm by the route known to work.
        $entry = TheAlarmEntry $session
        if ($entry) {
            Wire 'POST' "/session/$session/moveto" "{`"element`":`"$entry`"}" | Out-Null
            Wire 'POST' "/session/$session/click" '{"button":2}' | Out-Null
            Start-Sleep -Seconds 2
            $delete = ByName $session 'Delete'
            if ($delete) { Wire 'POST' "/session/$session/element/$delete/click" '{}' | Out-Null }
            Start-Sleep -Seconds 2
        }

        Wire 'DELETE' "/session/$session" '{}' | Out-Null
    }
    finally {
        if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
        Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

Measure 'THE REFERENCE (WinAppDriver)' $reference $false
Measure 'THIS DRIVER' $ours $true

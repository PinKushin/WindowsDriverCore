<#
    Does the hour selector reach 8 late, or land on 7 and stay?

    ClickElement fails with Expected:<8>. Actual:<7>.

        hourSelector.FindElementByName("8").Click()
        Assert.AreEqual("8", hourSelector.Text)      // no wait

    7 is ADJACENT to 8, which is what reading a LoopingSelector mid-snap looks
    like - it passes through 7 on its way. But a click that selected the wrong
    item predicts exactly the same single observation, and LIMITATIONS already
    carries a candidate fix for the first that has deliberately not been taken.

    ONE READING CANNOT TELL THEM APART. A SEQUENCE CAN:

        7 7 7 8 8 8   the value arrives late          -> settle it
        7 7 7 7 7 7   the click chose the wrong item  -> a settle fixes nothing
        8 8 8 8 8 8   neither, and the failure is elsewhere

    So this samples hourSelector.Text on a schedule after the click and prints
    the whole series with timings, rather than asking whether it is 8 yet.

    WHY IT MATTERS THAT THIS IS MEASURED FIRST. The obvious fix is to reuse
    WaitForValueToSettle, which requires an observed CHANGE before it accepts
    stability - so a click selecting an ALREADY-SELECTED item would spend its
    whole budget, on every selection click in every suite. That is the shape of
    a fix costing more than the bug, which cost this project twelve tests once.
    The series says whether a settle is even the right instrument.

    A CONTROL IN THE SAME RUN: click an item that is ALREADY selected and sample
    the same way. That is the case the naive settle would punish, and its series
    says how long a no-change click would have to wait.
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

function ChildByName([string] $session, [string] $parent, [string] $name) {
    (Value (Wire 'POST' "/session/$session/element/$parent/element" "{`"using`":`"name`",`"value`":`"$name`"}")).ELEMENT
}

function TextOf([string] $session, [string] $element) {
    Value (Wire 'GET' "/session/$session/element/$element/text" '{}')
}

# THE SERIES. Sampled as fast as a read allows, with the elapsed time beside
# each, so "late" and "never" are different pictures rather than one bit.
function SampleAfterClicking([string] $session, [string] $selector, [string] $itemName) {
    $item = ChildByName $session $selector $itemName
    if (-not $item) { return "  no item named '$itemName' in the selector" }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Wire 'POST' "/session/$session/element/$item/click" '{}' | Out-Null
    $clickCost = $watch.Elapsed.TotalMilliseconds

    $series = @()
    while ($watch.Elapsed.TotalMilliseconds -lt 2500) {
        $series += "{0}@{1}" -f (TextOf $session $selector), [int]$watch.Elapsed.TotalMilliseconds
        Start-Sleep -Milliseconds 40
    }

    "  click returned at {0:N0} ms" -f $clickCost
    "  " + ($series -join '  ')
}

try {
    $ready = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $ready = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $ready) { 'ABORT: driver never answered /status'; return }

    $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
    if (-not $session) { 'ABORT: no Alarms session'; return }
    Start-Sleep -Seconds 3

    $add = ById $session 'AddAlarmButton'
    if (-not $add) { 'ABORT: no AddAlarmButton - the alarm list may be at its cap'; return }
    Wire 'POST' "/session/$session/element/$add/click" '{}' | Out-Null
    Start-Sleep -Seconds 2

    $hour = ById $session 'HourLoopingSelector'
    if (-not $hour) { 'ABORT: no HourLoopingSelector'; return }

    $start = TextOf $session $hour
    "hour selector starts at '$start'"

    # THE SUBJECT: the suite clicks 8. If the selector already reads 8 the test
    # itself would be measuring nothing, so say so rather than report a series.
    ''
    "clicking '8' (the value the suite asks for):"
    if ($start -eq '8') {
        "  SKIPPED - the selector already reads 8, so this case cannot show a change"
    }
    else {
        SampleAfterClicking $session $hour '8'
    }

    # THE CONTROL: click whatever it now reads, which changes nothing. This is
    # the case a change-then-stability settle would punish.
    $now = TextOf $session $hour
    ''
    "clicking '$now' again (CONTROL - nothing should change):"
    SampleAfterClicking $session $hour $now

    Wire 'DELETE' "/session/$session" '{}' | Out-Null
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
}

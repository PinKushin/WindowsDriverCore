<#
    Why does NavigateBack_SystemApp read the wrong title?

    THE TEST, reduced (Tests/WebDriverAPI/Back.cs):

        originalTitle = session.Title                  // "File Explorer"
        SendKeys(Alt+D, testFolder, Enter); sleep 1s   // -> "Temp"
        session.Navigate().Back()
        Assert.AreEqual(originalTitle, session.Title)  // NO SLEEP

    We fail it with Actual "Temp". Note the asymmetry in the test itself: the
    step that navigates FORWARD sleeps a full second before reading, and the
    step that navigates BACK reads immediately. That is the shape of a race the
    reference wins by being slow, which this project has measured before -
    MouseClick took the reference 3.9 s and us 0.067 s, and we failed it.

    BUT IT IS A HYPOTHESIS, and the competing one predicts the same failure: our
    Alt+Left may simply not reach Explorer at all, in which case no amount of
    waiting helps and a drain would be a fix for the wrong defect.

    THE TWO ARE DISTINGUISHED BY WHEN THE TITLE CHANGES, so this reads it on a
    schedule rather than once:

        immediately after /back, then at ~100 ms intervals to 2 s

    A title that arrives late is a race. A title that never arrives is a gesture
    that did not land. The reference is measured the same way in the same run, so
    "how long does it need" is answered rather than guessed - including how long
    its own POST /back takes, which is where it may be absorbing the delay.

    IT MODIFIES NOTHING PERSISTENT: it opens Explorer, navigates to a temp
    folder, goes back, and closes the window.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ours = 'C:\baseline\host\WindowsDriverCore.exe'
$reference = 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'

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

# NOT NAMED `Measure`. PowerShell resolves ALIASES BEFORE FUNCTIONS, and
# `Measure` is a built-in alias for Measure-Object - so a function by that name
# is never called and the arguments land on Measure-Object instead:
#
#   Measure-Object : A positional parameter cannot be found that accepts
#   argument 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
#
# It cost two probe runs. Alias > Function > Cmdlet > Application.
function MeasureDriver([string] $label, [string] $exe) {
    Get-Process WindowsDriverCore, WinAppDriver -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process explorer -ErrorAction SilentlyContinue | Out-Null
    Start-Sleep -Milliseconds 600

    $server = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
    try {
        $up = $false
        foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $up = $true; break }; Start-Sleep -Seconds 1 }
        if (-not $up) { "ABORT: $label never answered /status"; return }

        ''
        "=== $label ==="

        $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"C:\\Windows\\explorer.exe"}}' | ConvertFrom-Json).sessionId
        if (-not $session) { "ABORT: $label made no Explorer session"; return }

        Start-Sleep -Seconds 2
        $original = Value (Wire 'GET' "/session/$session/title" '{}')
        "  original title : '$original'"

        # Alt+D focuses the address bar; the path and Enter navigate. Exactly the
        # keys the suite sends, through the same /keys route.
        $keys = '{"value":["\uE00A","d","\uE00A","C:\\Windows\\Temp\n"]}'
        Wire 'POST' "/session/$session/keys" $keys | Out-Null
        Start-Sleep -Seconds 2

        $moved = Value (Wire 'GET' "/session/$session/title" '{}')
        "  after navigate : '$moved'"

        if ($moved -eq $original) {
            "  ABORT for $label`: the forward navigation did not change the title, so"
            "  the back step has nothing to undo and this measures nothing."
            Wire 'DELETE' "/session/$session" '{}' | Out-Null
            return
        }

        # THE MEASUREMENT. How long does /back itself take, and when does the
        # title actually come back?
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        Wire 'POST' "/session/$session/back" '{}' | Out-Null
        $backCost = $watch.Elapsed.TotalMilliseconds

        $firstCorrect = $null
        $samples = @()
        foreach ($i in 0..20) {
            $title = Value (Wire 'GET' "/session/$session/title" '{}')
            $at = [int]$watch.Elapsed.TotalMilliseconds
            $samples += "{0,5} ms  '{1}'" -f $at, $title
            if ($null -eq $firstCorrect -and $title -eq $original) { $firstCorrect = $at }
            if ($firstCorrect -ne $null -and $at -gt ($firstCorrect + 200)) { break }
            Start-Sleep -Milliseconds 100
        }

        "  POST /back took : {0:N1} ms" -f $backCost
        if ($null -eq $firstCorrect) {
            "  TITLE NEVER CAME BACK within $([int]$watch.Elapsed.TotalMilliseconds) ms - the gesture did not land"
        }
        else {
            "  title correct at: $firstCorrect ms after the request was SENT"
        }
        '  samples:'
        $samples | ForEach-Object { "    $_" }

        Wire 'DELETE' "/session/$session" '{}' | Out-Null
    }
    finally {
        if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    }
}

MeasureDriver 'THE REFERENCE (WinAppDriver)' $reference
MeasureDriver 'THIS DRIVER' $ours

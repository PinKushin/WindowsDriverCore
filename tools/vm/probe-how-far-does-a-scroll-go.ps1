# HOW FAR DOES THE SAME SCROLL GESTURE ACTUALLY MOVE THE SELECTOR?
#
# The three scroll tests are the bulk of our flake AND three of the thirteen
# backlog entries. All of them drive MinuteLoopingSelector and assert whether the
# "00" minute is visible; both directions fail in different runs, which is a
# gesture landing ON a threshold rather than a systematic under- or over-scroll.
#
# THE REFERENCE IS DETERMINISTIC HERE. Across five WinAppDriver runs the scroll
# tests never moved once - the only movers were two SendKeys tests and one
# stale-element test. So WinAppDriver produces a gesture the LoopingSelector
# resolves the same way every time and we produce one on the boundary.
#
# PASS/FAIL CANNOT TELL US WHY. The suite's own instrument is "is 00 visible",
# which is binary and saturates: it cannot distinguish "moved just barely
# enough" from "moved five times as far". This measures the MAGNITUDE instead -
# the same /actions payload to both drivers, and the selector's own reported
# value before and after.
#
# Two mechanisms for this family have already been proposed and both died: the
# overrun theory (refuted - frames fit their slot at 1.0x) and dirty start state
# (refuted - two runs of identical code after full runs still differed by three
# tests). This probe exists so the third idea starts from a number.
#
# REPEATED, because the whole point is that our result varies. A single row from
# each driver would compare one sample of a distribution against one sample of a
# constant and prove nothing about the spread.

$ErrorActionPreference = 'Continue'

# GUEST ONLY. It drives a real application through a real driver.
if (-not (Test-Path 'C:\baseline')) {
    throw 'REFUSED: this probe drives Alarms & Clock and must run in the Win10 guest.'
}

$base = 'http://127.0.0.1:4723'
$ALARMS = 'Microsoft.WindowsAlarms_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

function Find($session, $id) {
    try { (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT }
    catch { $null }
}

# WHAT THE SELECTOR SAYS ABOUT ITSELF. Which of these carries the position is
# not known in advance, so all three are printed on the first run and the
# informative one can be relied on afterwards. Guessing the instrument is how a
# probe ends up measuring something that never changes.
function SelectorState($session, $sel) {
    $out = @{}
    foreach ($what in 'Value', 'Name') {
        try {
            $out[$what] = (Invoke-RestMethod -TimeoutSec 20 `
                -Uri "$base/session/$session/element/$sel/attribute/$what").value
        } catch { $out[$what] = '(err)' }
    }
    try {
        $out['Text'] = (Invoke-RestMethod -TimeoutSec 20 `
            -Uri "$base/session/$session/element/$sel/text").value
    } catch { $out['Text'] = '(err)' }
    $out
}

# NOT named Measure: that is an alias for Measure-Object, so the call binds to
# the cmdlet and dies with "a positional parameter cannot be found". Second
# alias collision in this project after Clear/Clear-Host - a probe function
# named after a PowerShell verb will be shadowed, silently or loudly.
function MeasureDriver($driverName, $exePath) {
    ''
    '=========================================================='
    "  $driverName"
    '=========================================================='

    Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    $srv = Start-Process -FilePath $exePath -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    try {
        $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$ALARMS`"}}").sessionId
        if (-not $session) { '  no session'; return }
        Start-Sleep -Seconds 3

        # Three attempts, because our result is the thing that varies.
        for ($round = 1; $round -le 3; $round++) {

            # Navigate to the time picker, fresh each round.
            $add = Find $session 'AddAlarmButton'
            if (-not $add) { "  round ${round}: no AddAlarmButton"; continue }
            try { PostRaw "/session/$session/element/$add/click" '{}' | Out-Null } catch { }
            Start-Sleep -Milliseconds 800

            $picker = Find $session 'AlarmTimePicker'
            if ($picker) {
                try { PostRaw "/session/$session/element/$picker/click" '{}' | Out-Null } catch { }
            }
            Start-Sleep -Seconds 1

            $sel = Find $session 'MinuteLoopingSelector'
            if (-not $sel) { "  round ${round}: no MinuteLoopingSelector"; continue }

            $before = SelectorState $session $sel
            if ($round -eq 1) {
                "  readable attributes: Value='$($before.Value)' Name='$($before.Name)' Text='$($before.Text)'"
            }

            # THE SUITE'S OWN GESTURE: element origin, contact down, move 200 px
            # up over 500 ms, lift.
            #
            # THE W3C ELEMENT KEY, NOT THE JWP ONE. The dialect split is PER
            # COMMAND: the suite is Selenium 3.8, which speaks JWP for classic
            # commands and W3C inside /actions. A JWP "ELEMENT" origin is
            # REFUSED by WinAppDriver - measured here, three rounds out of
            # three, before this comment existed. We accept both, so sending the
            # JWP spelling looks fine right up until the reference is asked the
            # same question.
            $body = @"
{"actions":[{"type":"pointer","id":"finger","parameters":{"pointerType":"touch"},"actions":[
 {"type":"pointerMove","origin":{"ELEMENT":"$sel","element-6066-11e4-a52e-4f735466cecf":"$sel"},"x":0,"y":0},
 {"type":"pointerDown","button":0},
 {"type":"pointerMove","origin":{"ELEMENT":"$sel","element-6066-11e4-a52e-4f735466cecf":"$sel"},"x":0,"y":-200,"duration":500},
 {"type":"pointerUp","button":0}]}]}
"@
            # THE BODY, READ OFF THE RESPONSE STREAM. "REFUSED" alone says a
            # request failed and nothing about why, which cost two rounds of
            # guessing at the payload shape. Invoke-RestMethod does not populate
            # ErrorDetails for a 400 with a JSON body on Windows PowerShell 5.1,
            # so the stream is the only reliable source.
            $note = 'ok'
            try { PostRaw "/session/$session/actions" $body | Out-Null }
            catch {
                $note = 'REFUSED'
                $resp = $_.Exception.Response
                if ($resp) {
                    try {
                        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                        $b = $reader.ReadToEnd(); $reader.Close()
                        if ($b) { $note = 'REFUSED: ' + ($b -replace '\s+', ' ') }
                    } catch { }
                }
            }
            Start-Sleep -Seconds 1

            $after = SelectorState $session $sel

            # "00" visibility as well, so the magnitude can be lined up against
            # the assertion the suite actually makes.
            $zeroVisible = '?'
            try {
                $z = (PostRaw "/session/$session/element" '{"using":"name","value":"00"}').value.ELEMENT
                if ($z) {
                    $zeroVisible = (Invoke-RestMethod -TimeoutSec 20 `
                        -Uri "$base/session/$session/element/$z/displayed").value
                }
            } catch { $zeroVisible = 'not found' }

            '  round {0}: before Value=''{1}''  after Value=''{2}''   00 displayed={3}   {4}' -f `
                $round, $before.Value, $after.Value, $zeroVisible, $note

            # Leave the picker so the next round starts from the list.
            foreach ($id in 'CancelButton', 'Back') {
                $c = Find $session $id
                if ($c) { try { PostRaw "/session/$session/element/$c/click" '{}' | Out-Null } catch { }; break }
            }
            Start-Sleep -Milliseconds 800
        }

        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
    }
    finally {
        Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
        Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

MeasureDriver 'WinAppDriver 1.2.1 (the reference)' 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
MeasureDriver 'WindowsDriverCore (ours)'           'C:\baseline\host\WindowsDriverCore.exe'

''
'=== probe complete ==='
''
'READ IT LIKE THIS:'
'  reference moves further, consistently -> our gesture is too weak; the fling'
'                                           velocity at release is the thing to'
'                                           compare next.'
'  both move the same, ours varies       -> the magnitude is not the difference'
'                                           and the boundary is elsewhere.'
'  ours varies round to round            -> that IS the flake, reproduced in a'
'                                           one-minute probe instead of a'
'                                           25-minute suite run.'

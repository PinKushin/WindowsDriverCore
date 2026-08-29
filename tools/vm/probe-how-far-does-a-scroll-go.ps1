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
function MeasureDriver($driverName, $exePath, $cases) {
    ''
    '=========================================================='
    "  $driverName"
    '=========================================================='

    Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    # Set BEFORE the driver starts, and echoed, because a sweep whose value
    # never reached the process looks exactly like a value that made no
    # difference.
    # Cleared first: an env var set for a previous driver would otherwise
    # persist into this one and the row would report the wrong condition.
    Remove-Item Env:\WDC_SCROLL_MS -ErrorAction SilentlyContinue
    if ($null -ne $cases[0].Ms) {
        $env:WDC_SCROLL_MS = "$($cases[0].Ms)"
        "  driver env: WDC_SCROLL_MS=$($cases[0].Ms)"
    }

    $srv = Start-Process -FilePath $exePath -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    try {
        $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$ALARMS`"}}").sessionId
        if (-not $session) { '  no session'; return }
        Start-Sleep -Seconds 3

        # TWO CANDIDATE EXPLANATIONS, AND THE PAIR OF VARIABLES SEPARATES THEM.
        #
        # The reference hides "00" with a -55 scroll and we do not, both
        # deterministically. Either we scroll LITERALLY the offset while it adds
        # momentum - in which case a bigger offset works and the duration does
        # not matter - or our gesture is too slow to fling at all, in which case
        # a shorter duration works at the same -55.
        #
        # A sweep of one variable could not tell those apart.
        foreach ($case in $cases) {
            $round = "$($case.Offset)px/$($case.Ms)ms"

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

            # /touch/scroll, WHICH IS JWP AND THE REFERENCE ACCEPTS IT.
            #
            # The /actions form was refused by WinAppDriver three rounds out of
            # three and the reason is still unknown - and chasing it is beside
            # the point, because TouchScrollOnElement_Vertical uses THIS route
            # and the reference passes it. Asking both drivers the same question
            # on a route both answer is worth more than making the other payload
            # work.
            $note = 'ok'
            try {
                PostRaw "/session/$session/touch/scroll" `
                    "{`"element`":`"$sel`",`"xoffset`":0,`"yoffset`":$($case.Offset)}" | Out-Null
            }
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

            # WHERE IT LANDED, which is the whole measurement. The three scroll
            # tests share one session and one selector and nothing resets its
            # position between them, so the item this comes to rest on is what
            # the NEXT test inherits. A LoopingSelector snaps to an item; if we
            # land somewhere the reference does not, the difference compounds.
            $picker = Find $session 'AlarmTimePicker'
            $landed = '(no picker)'
            if ($picker) {
                try {
                    $landed = (Invoke-RestMethod -TimeoutSec 20 `
                        -Uri "$base/session/$session/element/$picker/attribute/Name").value
                } catch { $landed = '(err)' }
            }

            $zeroVisible = '?'
            try {
                $z = (PostRaw "/session/$session/element" '{"using":"name","value":"00"}').value.ELEMENT
                if ($z) {
                    $zeroVisible = (Invoke-RestMethod -TimeoutSec 20 `
                        -Uri "$base/session/$session/element/$z/displayed").value
                }
            } catch { $zeroVisible = 'not found' }

            '  round {0}: landed on ''{1}''   00 displayed={2}   {3}' -f `
                $round, $landed, $zeroVisible, $note

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

$REF  = 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
$OURS = 'C:\baseline\host\WindowsDriverCore.exe'

# The reference at the suite's own values: the control, and the thing to match.
MeasureDriver 'reference: -55px' $REF @(
    @{ Offset = -55; Ms = $null }
    @{ Offset = -55; Ms = $null }
    @{ Offset = -55; Ms = $null }
)

# Ours, with ONE variable moved at a time. Two candidate explanations and this
# is what separates them: either we scroll LITERALLY the offset while the
# reference adds momentum - so a bigger offset works and the duration does not
# matter - or our gesture is too slow to fling at all, so a shorter duration
# works at the same -55. A sweep of one variable could not tell those apart.
# WHERE IS THE FLING THRESHOLD? Both knobs moved the result in the same
# direction, so the variable is VELOCITY: 55 px in 300 ms is 183 px/s and does
# not fling, 55 px in 60 ms is 917 px/s and does. This walks the duration at a
# fixed offset to find the boundary, so the shipped value is chosen with a
# measured margin rather than because it was the one number tried.
#
# Each row is a SEPARATE driver start, because the duration is read from the
# environment once at startup.
# THREE ATTEMPTS PER CONDITION, and the previous version of this had one.
#
# A single sample per duration produced a clean velocity story - 300 ms no,
# 60 ms yes, 1000 ms no, a bigger offset yes - which a wider single-sample sweep
# then contradicted non-monotonically: 60 yes, 100 no, 150 no, 200 yes, 250 yes,
# 300 no. Velocity cannot order that. The outcome is stochastic at these values,
# so one row per condition was measuring the coin rather than the condition.
#
# The duration is read from the environment once at startup, so a driver start
# per condition with three rounds inside it is the shape that gives repeats.
foreach ($ms in 60, 150, 300) {
    MeasureDriver "ours: -55px / ${ms}ms" $OURS @(
        @{ Offset = -55; Ms = $ms }
        @{ Offset = -55; Ms = $ms }
        @{ Offset = -55; Ms = $ms }
    )
}

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

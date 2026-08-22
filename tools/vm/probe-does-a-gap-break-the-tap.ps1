# DOES A GAP BETWEEN /touch/down AND /touch/up STILL PRODUCE A TAP?
#
# THE FLAW THIS FIXES IS IN MY OWN EARLIER PROBE. The hold test concluded "a
# contact survives 400 ms" from the fact that /touch/up returned ok. That is the
# same mistake as the drag investigation, one level down: a tap that lands
# nowhere ALSO returns ok. The lift succeeding says nothing about whether the
# system saw a press.
#
# Calculator gives a real observable. TouchDownMoveUp_SingleTap taps num8Button
# and asserts the display reads "8", and it PASSES - so a back-to-back
# down/up through /touch/* genuinely registers. The question is what a GAP does
# to that, and it has never been asked with an assertion attached.
#
#   gap 0 ms     the known-good control. If this fails, the harness is wrong and
#                nothing else here means anything.
#   gap 50..800  if the display stops reading "8", an injected contact does not
#                survive being left alone between requests - which would explain
#                the drag directly, since a drag is three requests with gaps.
#
# THE DISPLAY IS THE MEASUREMENT, not the HTTP status. Every earlier conclusion
# in this investigation that rested on a status code is suspect for exactly this
# reason.

$ErrorActionPreference = 'Continue'

if (-not (Test-Path 'C:\baseline')) { throw 'REFUSED: guest only.' }

$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

function Display($session, $results) {
    $t = Invoke-RestMethod -Uri "$base/session/$session/element/$results/text" -TimeoutSec 20
    return ($t.value -replace 'Display is', '').Trim()
}

Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process Calculator, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$srv = Start-Process -FilePath 'C:\baseline\host\WindowsDriverCore.exe' -PassThru
for ($i = 0; $i -lt 40; $i++) {
    try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
}

try {
    $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
    if (-not $session) { throw 'ABORT: no session' }
    Start-Sleep -Seconds 3

    $num8    = (PostRaw "/session/$session/element" '{"using":"accessibility id","value":"num8Button"}').value.ELEMENT
    $clear   = (PostRaw "/session/$session/element" '{"using":"accessibility id","value":"clearButton"}').value.ELEMENT
    $results = (PostRaw "/session/$session/element" '{"using":"accessibility id","value":"CalculatorResults"}').value.ELEMENT
    if (-not $num8 -or -not $clear -or -not $results) { throw 'ABORT: Calculator elements not found' }

    function CentreOf($el) {
        $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$el/location" -TimeoutSec 20
        $sz  = Invoke-RestMethod -Uri "$base/session/$session/element/$el/size" -TimeoutSec 20
        return @{
            X = [int]$loc.value.x + [int]([int]$sz.value.width / 2)
            Y = [int]$loc.value.y + [int]([int]$sz.value.height / 2)
        }
    }

    $p8 = CentreOf $num8
    $pc = CentreOf $clear

    '  gap(ms)   display after tapping 8    verdict'
    '  -------   -----------------------    -------'

    foreach ($gap in 0, 50, 100, 200, 400, 800) {
        # Clear first, so each row starts from a known display.
        PostRaw "/session/$session/touch/down" "{`"x`":$($pc.X),`"y`":$($pc.Y)}" | Out-Null
        PostRaw "/session/$session/touch/up"   "{`"x`":$($pc.X),`"y`":$($pc.Y)}" | Out-Null
        Start-Sleep -Milliseconds 400

        $status = 'ok'
        try { PostRaw "/session/$session/touch/down" "{`"x`":$($p8.X),`"y`":$($p8.Y)}" | Out-Null }
        catch { $status = 'down failed' }

        if ($gap -gt 0) { Start-Sleep -Milliseconds $gap }

        if ($status -eq 'ok') {
            try { PostRaw "/session/$session/touch/up" "{`"x`":$($p8.X),`"y`":$($p8.Y)}" | Out-Null }
            catch { $status = 'UP REFUSED' }
        }

        Start-Sleep -Milliseconds 500
        $shown = Display $session $results
        $verdict = if ($shown -eq '8') { 'TAP REGISTERED' } else { "NO TAP ($status)" }

        '  {0,7}   {1,-23}    {2}' -f $gap, $shown, $verdict
    }

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process Calculator, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='

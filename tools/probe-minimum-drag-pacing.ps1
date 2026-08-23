# HOW LITTLE PACING DOES THE DRAG ACTUALLY NEED?
#
# 300 ms is inherited from the /actions path and was never tested for the
# multi-request one. It got defended by pointing at WinAppDriver spending ~100 ms
# per touch phase - but that measurement was taken while timing was still
# believed to be the CAUSE, and once the cause turned out to be the injection API
# it stopped being evidence for anything.
#
# What is actually measured is only that SOME pacing is needed: with the WinRT
# injector in place, a single /touch/move moved the window at 300 ms and did not
# at zero. Everything between is unexplored, and 300 ms is a cost paid on every
# multi-request touch move.
#
# ONE /touch/move PER ROW - the shape the compatibility suite sends, and the only
# case that still needed pacing. The driver reads WDC_DRAG_MS, so this sweeps
# without a rebuild per value.
#
# RE-RUN 2026-08-22, AND THE FIRST SWEEP'S NUMBERS MEANT SOMETHING ELSE. It read
# a cliff between 10 and 25 ms and was reported as "roughly 2 ms of separation per
# frame". It was not: each frame slept "the remainder of this frame" and Windows
# wakes a sleeper on a ~15.6 ms tick, so a request for 2.5 ms became 15 ms and
# every overshoot accumulated. The rows below 15 ms were measuring the ROUNDING,
# not a threshold - at 10 ms the remainder rounded to zero and no sleep happened
# at all, which is why it read as a burst.
#
# The deadline is now computed from the start of the move, so a requested
# interval is delivered to within 0.1 ms and these numbers say what they appear
# to. This sweep is the honest version of the same question.
#
# RESULT, 2026-08-22, repeats interleaved rather than batched so drift over the
# run cannot masquerade as a threshold:
#
#     total   per frame   moved
#      0 ms       0 ms    no x3         <- the control
#     20 ms       2 ms    3 YES / 5 no     marginal
#     40 ms       4 ms    YES x5
#     50 ms       5 ms    YES x3        <- shipped
#     60 ms       6 ms    YES x3
#     80 ms       8 ms    YES x2
#
# Threshold between 2 and 4 ms per frame; 50 ms ships because that is where a
# gesture stops being perceptible as a delay. Both 20 ms results are kept: one
# observation is not a measurement, and dropping the rows that disagree is how a
# range gets quietly reported as a point.
#
# THE ZERO ROW IS A CONTROL, NOT A DATA POINT, AND MUST NOT BE REMOVED. A sweep of
# only working values reads identically whether the override is wired or not - the
# rows all say YES either way, at whatever the compiled default happens to be.
# That exact ambiguity was produced once by narrowing this list.
#
# The window is restored after every row: without that the subject walks toward
# the corner and eventually trips Snap Assist on a real desktop.

$ErrorActionPreference = 'Continue'

$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

$binRoot = Join-Path $PSScriptRoot '..' | Join-Path -ChildPath 'src/WindowsDriverCore.Host/bin'
$exe = Get-ChildItem -Path $binRoot -Filter 'WindowsDriverCore.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'ABORT: no built driver' }

# THE EXE IS AN APPHOST AND ITS TIMESTAMP NEVER MOVES. Reporting it says a probe
# is running fresh code when it may not be - the managed code lives in the DLL
# beside it, and that is what a rebuild rewrites. Cost a confusing reading once.
$dll = Join-Path (Split-Path $exe) 'WindowsDriverCore.Protocol.dll'
"driver:   $exe"
"apphost:  $((Get-Item $exe).LastWriteTime)  (does not move; ignore it)"
"code:     $((Get-Item $dll).LastWriteTime)  <- this is the build under test"
''
'  drag ms   moved?   note'
'  -------   ------   ----'

foreach ($ms in 0, 20, 40, 50, 60, 80) {
    Get-Process WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process CalculatorApp, Calculator -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2

    $env:WDC_DRAG_MS = "$ms"
    $srv = Start-Process -FilePath $exe -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    try {
        $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
        if (-not $session) { "  {0,7}   (no session)" -f $ms; continue }
        Start-Sleep -Seconds 3

        $title = $null
        foreach ($id in 'AppName','TitleBar') {
            try {
                $title = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT
                if ($title) { break }
            } catch { }
        }
        if (-not $title) { "  {0,7}   (no title bar)" -f $ms; continue }

        $before = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
        $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$title/location" -TimeoutSec 20
        $sz  = Invoke-RestMethod -Uri "$base/session/$session/element/$title/size" -TimeoutSec 20
        $x = [int]$loc.value.x + [int]([int]$sz.value.width / 2)
        $y = [int]$loc.value.y + [int]([int]$sz.value.height / 2)

        $note = 'ok'
        try { PostRaw "/session/$session/touch/down" "{`"x`":$x,`"y`":$y}" | Out-Null } catch { $note = 'down failed' }
        try { PostRaw "/session/$session/touch/move" "{`"x`":$($x+100),`"y`":$($y+100)}" | Out-Null } catch { $note = 'move failed' }
        try { PostRaw "/session/$session/touch/up"   "{`"x`":$($x+100),`"y`":$($y+100)}" | Out-Null } catch { $note = 'UP REFUSED' }

        Start-Sleep -Seconds 1
        $after = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
        $moved = ($before.value.x -ne $after.value.x) -or ($before.value.y -ne $after.value.y)

        '  {0,7}   {1,-6}   {2}' -f $ms, $(if ($moved) { 'YES' } else { 'no' }), $note

        try {
            PostRaw "/session/$session/window/current/position" `
                "{`"x`":$($before.value.x),`"y`":$($before.value.y)}" | Out-Null
        } catch { }

        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
    }
    finally {
        Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
        Remove-Item Env:\WDC_DRAG_MS -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

Get-Process CalculatorApp, Calculator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
''
'=== probe complete ==='

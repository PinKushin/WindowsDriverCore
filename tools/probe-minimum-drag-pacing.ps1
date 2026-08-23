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
"driver built: $((Get-Item $exe).LastWriteTime)"
''
'  drag ms   moved?   note'
'  -------   ------   ----'

foreach ($ms in 0, 10, 25, 50, 100, 200, 300) {
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

# Does the multi-request drag fail on THIS machine too, or only on the guest?
#
# Everything so far was measured on the Windows 10 guest against Alarms & Clock
# 10.1906.2182.0. That is one OS and one app version, and "the drag does not
# work" has never been checked anywhere else. If it fails here as well, it is a
# defect in this driver. If it WORKS here, the guest is telling us something
# about Windows 10 or that app rather than about the code.
#
# BOTH PATHS, SAME WINDOW, SAME RUN:
#
#   /actions        one request, element origin, paced move - PASSES on the guest
#   /touch/down|move|up   three requests - FAILS on the guest
#
# Running both here is what makes the comparison mean anything: if /actions moves
# the window and /touch/* does not, the split reproduces and is about this
# driver. If NEITHER moves it, the host differs from the guest and the guest
# result needs re-reading.
#
# IT MOVES A REAL WINDOW ON THIS DESKTOP. It launches its own Calculator, drags
# that, and closes it - it never touches anything the user opened. Run it under
# run-exclusive.ps1 so it cannot collide with another agent's UI work, exactly as
# the SynthesisesRealInput test fixtures are.

$ErrorActionPreference = 'Continue'

$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

# THE NEWEST BUILD, FOUND RATHER THAN ASSUMED.
#
# A hardcoded bin path measured a STALE BINARY once already: changing the
# TargetFramework to net10.0-windows10.0.17763.0 moved the output directory, the
# old net10.0-windows folder still held a working exe from 24 minutes earlier,
# and the probe reported on code that no longer existed. Same hazard as
# --no-build, arriving through a different door.
$binRoot = Join-Path $PSScriptRoot '..' | Join-Path -ChildPath 'src/WindowsDriverCore.Host/bin'
$exe = Get-ChildItem -Path $binRoot -Filter 'WindowsDriverCore.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'ABORT: no built driver under src/WindowsDriverCore.Host/bin' }

"driver: $exe"
"windows: $([System.Environment]::OSVersion.Version)"

$srv = Start-Process -FilePath $exe -PassThru
for ($i = 0; $i -lt 40; $i++) {
    try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
}

# Puts the window back where it started.
#
# Not tidiness - a probe that leaves the subject 100 px further into the corner
# every iteration is changing its own conditions AND, on a real desktop, dragging
# a window into the Snap Assist trigger zone.
function RestorePosition($session, $before) {
    try {
        PostRaw "/session/$session/window/current/position" `
            "{`"x`":$($before.value.x),`"y`":$($before.value.y)}" | Out-Null
    } catch { }
}

function DragOnce($label, $useActions) {
    $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
    if (-not $session) { "  $label : no session"; return }
    Start-Sleep -Seconds 3

    try {
        $title = $null
        foreach ($id in 'AppName','AppNameTitle','TitleBar') {
            try {
                $title = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT
                if ($title) { break }
            } catch { }
        }
        if (-not $title) { "  $label : no title bar element"; return }

        $before = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
        $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$title/location" -TimeoutSec 20
        $sz  = Invoke-RestMethod -Uri "$base/session/$session/element/$title/size" -TimeoutSec 20
        $x = [int]$loc.value.x + [int]([int]$sz.value.width / 2)
        $y = [int]$loc.value.y + [int]([int]$sz.value.height / 2)

        $status = 'ok'
        if ($useActions) {
            $body = @"
{"actions":[{"type":"pointer","id":"finger1","parameters":{"pointerType":"touch"},"actions":[
{"type":"pointerMove","duration":0,"origin":{"element-6066-11e4-a52e-4f735466cecf":"$title"},"x":0,"y":0},
{"type":"pointerDown","button":0},
{"type":"pointerMove","duration":1000,"origin":"pointer","x":100,"y":100},
{"type":"pointerUp","button":0}]}]}
"@
            try { PostRaw "/session/$session/actions" $body | Out-Null } catch { $status = 'actions failed' }
        }
        else {
            try { PostRaw "/session/$session/touch/down" "{`"x`":$x,`"y`":$y}" | Out-Null } catch { $status = 'down failed' }
            try { PostRaw "/session/$session/touch/move" "{`"x`":$($x+100),`"y`":$($y+100)}" | Out-Null } catch { $status = 'move failed' }
            try { PostRaw "/session/$session/touch/up"   "{`"x`":$($x+100),`"y`":$($y+100)}" | Out-Null } catch { $status = 'UP REFUSED' }
        }

        Start-Sleep -Seconds 1
        $after = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
        $moved = ($before.value.x -ne $after.value.x) -or ($before.value.y -ne $after.value.y)

        '  {0,-16} {1},{2} -> {3},{4}   MOVED: {5}   [{6}]' -f `
            $label, $before.value.x, $before.value.y, $after.value.x, $after.value.y, `
            $(if ($moved) { 'YES' } else { 'NO' }), $status

        # PUT IT BACK. Every drag here moves +100,+100 and a run does several, so
        # without this the window walks toward the bottom-right corner and
        # eventually trips Snap Assist over the user's desktop. The suite's own
        # drag test restores position for the same reason; this did not, and it
        # was noticed by someone watching the screen rather than by any output.
        RestorePosition $session $before
    }
    finally {
        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
        Start-Sleep -Seconds 2
    }
}

# THE MOVE'S DURATION, SWEPT. /actions paces its move over a FULL SECOND - ten
# frames a hundred milliseconds apart, which is slow deliberate movement - and it
# drags. Every multi-request attempt so far used 0 to 414 ms.
#
# Windows distinguishes a FLICK from a DRAG by speed, so a 100 px move completed
# in 300 ms may simply be the wrong gesture rather than a broken one. That was
# never tested: the paced attempts were abandoned because the LIFT failed, and
# the question of whether the window moved was never reached.
function SweepTouchDurations {
    foreach ($ms in 0, 250, 500, 1000, 1500) {
        $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
        if (-not $session) { "  sweep $ms : no session"; continue }
        Start-Sleep -Seconds 3

        try {
            $title = $null
            foreach ($id in 'AppName','AppNameTitle','TitleBar') {
                try {
                    $title = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT
                    if ($title) { break }
                } catch { }
            }
            if (-not $title) { "  sweep $ms : no title bar"; continue }

            $before = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
            $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$title/location" -TimeoutSec 20
            $sz  = Invoke-RestMethod -Uri "$base/session/$session/element/$title/size" -TimeoutSec 20
            $x = [int]$loc.value.x + [int]([int]$sz.value.width / 2)
            $y = [int]$loc.value.y + [int]([int]$sz.value.height / 2)

            $status = 'ok'
            try { PostRaw "/session/$session/touch/down" "{`"x`":$x,`"y`":$y}" | Out-Null } catch { $status = 'down failed' }

            # The move, walked HERE in the probe so the driver's own pacing is not
            # the variable - ten steps spread over the duration under test, which
            # is exactly the shape /actions produces internally.
            $steps = 10
            for ($i = 1; $i -le $steps; $i++) {
                $sx = $x + [int](100 * $i / $steps)
                $sy = $y + [int](100 * $i / $steps)
                try { PostRaw "/session/$session/touch/move" "{`"x`":$sx,`"y`":$sy}" | Out-Null } catch { $status = "move $i failed" }
                if ($ms -gt 0) { Start-Sleep -Milliseconds ([int]($ms / $steps)) }
            }

            try { PostRaw "/session/$session/touch/up" "{`"x`":$($x+100),`"y`":$($y+100)}" | Out-Null } catch { $status = 'UP REFUSED' }

            Start-Sleep -Seconds 1
            $after = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
            $moved = ($before.value.x -ne $after.value.x) -or ($before.value.y -ne $after.value.y)

            '  /touch/* over {0,5} ms   MOVED: {1,-3}   [{2}]' -f $ms, $(if ($moved) { 'YES' } else { 'NO' }), $status

            RestorePosition $session $before
        }
        finally {
            try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
            Start-Sleep -Seconds 2
        }
    }
}

try {
    ''
    DragOnce '/actions'   $true
    DragOnce '/touch/*'   $false
    ''
    '  sweeping the move duration, ten /touch/move requests spread over each:'
    SweepTouchDurations
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process CalculatorApp, Calculator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='

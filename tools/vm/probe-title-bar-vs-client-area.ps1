# Does dragging the TITLE BAR break the contact, or does moving break it?
#
# A CONFOUND IN THE PREVIOUS PROBE, found by reading it rather than by it
# failing. probe-reference-touch-drag's hold test touches the CLIENT AREA
# (60,120) and found our contact survives a bare 400 ms hold. The drag that
# fails touches the TITLE BAR. So "holding is fine, moving breaks it" was not
# actually established - the two experiments differ in target as well as in
# whether they move.
#
# That matters because a title-bar drag is not an ordinary gesture: DefWindowProc
# handles it by entering a NESTED MODAL MESSAGE LOOP that captures the pointer.
# A synthetic lift arriving while the window is inside that loop is a different
# situation from one arriving normally, and would explain both the refusal and
# why the window never ends up moved - the drag begins and the lift meant to end
# it is rejected.
#
# FOUR CELLS, one variable changed at a time:
#
#             no move          with move
#   client      A                 B
#   title bar   C                 D
#
# If only D fails, it is the title bar - the modal loop, and the fix is about how
# a window drag is performed. If B and D both fail, it is moving, and the target
# is irrelevant. If C and D both fail, it is the title bar even without moving.

$ErrorActionPreference = 'Continue'

if (-not (Test-Path 'C:\baseline')) {
    throw 'REFUSED: this probe injects real touch input and must only run in the Win10 guest (C:\baseline not found).'
}

$base = 'http://127.0.0.1:4723'
$ALARMS = 'Microsoft.WindowsAlarms_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$srv = Start-Process -FilePath 'C:\baseline\host\WindowsDriverCore.exe' -PassThru
for ($i = 0; $i -lt 40; $i++) {
    try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
}

try {
    $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$ALARMS`"}}").sessionId
    if (-not $session) { throw 'ABORT: no session' }
    Start-Sleep -Seconds 2

    # The title bar, the way the suite finds it.
    $title = $null
    foreach ($id in 'AppName','AppNameTitle','TitleBar') {
        try {
            $title = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT
            if ($title) { break }
        } catch { }
    }
    if (-not $title) { throw 'ABORT: no title bar element' }

    $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$title/location" -TimeoutSec 20
    $size = Invoke-RestMethod -Uri "$base/session/$session/element/$title/size" -TimeoutSec 20
    $titleX = [int]$loc.value.x + [int]($size.value.width / 2)
    $titleY = [int]$loc.value.y + [int]($size.value.height / 2)

    "  title bar at {0},{1}   client area at 60,120" -f $titleX, $titleY
    ''
    '  target       move?   up result'
    '  ----------   -----   ---------'

    foreach ($cell in @(
        @{ name = 'client';    x = 60;      y = 120;     move = $false }
        @{ name = 'client';    x = 60;      y = 120;     move = $true  }
        @{ name = 'title bar'; x = $titleX; y = $titleY; move = $false }
        @{ name = 'title bar'; x = $titleX; y = $titleY; move = $true  }
    )) {
        $status = 'ok'
        try { PostRaw "/session/$session/touch/down" "{`"x`":$($cell.x),`"y`":$($cell.y)}" | Out-Null }
        catch { $status = 'DOWN FAILED' }

        if ($cell.move -and $status -eq 'ok') {
            # Held long enough for a window manager to notice, which is the
            # condition the fast path never gives it.
            Start-Sleep -Milliseconds 150
            try { PostRaw "/session/$session/touch/move" "{`"x`":$($cell.x + 100),`"y`":$($cell.y + 100)}" | Out-Null }
            catch { $status = 'MOVE FAILED' }
            Start-Sleep -Milliseconds 150
        }
        elseif ($status -eq 'ok') {
            Start-Sleep -Milliseconds 300
        }

        if ($status -eq 'ok') {
            $ux = if ($cell.move) { $cell.x + 100 } else { $cell.x }
            $uy = if ($cell.move) { $cell.y + 100 } else { $cell.y }
            try { PostRaw "/session/$session/touch/up" "{`"x`":$ux,`"y`":$uy}" | Out-Null }
            catch { $status = 'UP REFUSED' }
        }

        '  {0,-10}   {1,-5}   {2}' -f $cell.name, $(if ($cell.move) { 'yes' } else { 'no' }), $status
        Start-Sleep -Milliseconds 800
    }

    # THE ONLY THING THE SUITE ACTUALLY ASSERTS: did the window move.
    #
    # "up ok" is not the result - TouchDownMoveUp_DragAndDrop checks the window's
    # POSITION, and a gesture can complete cleanly while dragging nothing. This
    # repeats the one cell that succeeded above and reports position either side.
    ''
    '  === does the successful shape actually MOVE the window? ==='
    $before = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
    "  before : x=$($before.value.x) y=$($before.value.y)"

    PostRaw "/session/$session/touch/down" "{`"x`":$titleX,`"y`":$titleY}" | Out-Null
    Start-Sleep -Milliseconds 150
    PostRaw "/session/$session/touch/move" "{`"x`":$($titleX + 100),`"y`":$($titleY + 100)}" | Out-Null
    Start-Sleep -Milliseconds 150
    PostRaw "/session/$session/touch/up" "{`"x`":$($titleX + 100),`"y`":$($titleY + 100)}" | Out-Null
    Start-Sleep -Seconds 1

    $after = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
    "  after  : x=$($after.value.x) y=$($after.value.y)"
    "  MOVED  : {0}" -f $(if ($before.value.x -ne $after.value.x -or $before.value.y -ne $after.value.y) { 'YES' } else { 'NO' })

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='
''
'  only the title-bar+move cell fails -> the modal move loop, and the fix is'
'                                        about how a window drag is performed.'
'  both move cells fail                -> moving breaks it, target irrelevant.'
'  both title-bar cells fail           -> the title bar breaks it even at rest.'

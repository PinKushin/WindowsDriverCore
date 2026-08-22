# ARE WE PRESSING THE TITLE BAR AT ALL?
#
# Twenty-five runs of timing hypotheses assumed the contact lands where intended
# and asked why the drag did not take. This asks the question underneath that
# one, which was never checked.
#
# THE TWO DRAG TESTS TARGET THE TITLE BAR BY DIFFERENT ROUTES:
#
#   Touch_DragAndDrop        PASSES   /actions with an ELEMENT origin - the
#                                     driver resolves the element and targets its
#                                     centre from UIA directly.
#   TouchDownMoveUp_DragAndDrop FAILS /touch/* - the CLIENT computes the centre
#                                     from our /location and /size and sends raw
#                                     coordinates, which we convert back to
#                                     screen using the window origin.
#
# So /actions never exercises the conversion that /touch/* depends on. If those
# two arrive at different pixels, every timing experiment was tuning a gesture
# that pressed the wrong thing.
#
# TouchDownMoveUp_SingleTap passes and asserts the display reads "8", so the
# conversion is right for a button INSIDE the app. The title bar is not inside
# the app - it belongs to the ApplicationFrameWindow chrome drawn by
# ApplicationFrameHost - and that case has never been checked.
#
# THE COMPARISON:
#
#   computed   what /touch/down would press: window origin + (/location + /size/2)
#   actual     the element's real screen rectangle, from the BoundingRectangle
#              attribute, which UIA reports in SCREEN coordinates
#
# Same for a client-area button as a CONTROL. If the button agrees and the title
# bar does not, the conversion is wrong specifically for frame chrome, and that
# is the whole defect.

$ErrorActionPreference = 'Continue'

if (-not (Test-Path 'C:\baseline')) { throw 'REFUSED: guest only.' }

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

    $pos = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
    "window origin reported by the driver: x=$($pos.value.x) y=$($pos.value.y)"
    ''
    '  element            /location   /size      computed press     actual BoundingRectangle'
    '  ----------------   ---------   --------   ----------------   ------------------------'

    foreach ($id in 'AppName', 'AlarmButton', 'StopwatchButton') {
        $el = $null
        try { $el = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT } catch { }
        if (-not $el) { "  {0,-16}   (not found)" -f $id; continue }

        $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$el/location" -TimeoutSec 20
        $size = Invoke-RestMethod -Uri "$base/session/$session/element/$el/size" -TimeoutSec 20
        $rect = Invoke-RestMethod -Uri "$base/session/$session/element/$el/attribute/BoundingRectangle" -TimeoutSec 20

        # What the CLIENT sends, and what we would then press.
        $cx = [int]$loc.value.x + [int]([int]$size.value.width / 2)
        $cy = [int]$loc.value.y + [int]([int]$size.value.height / 2)
        $pressX = [int]$pos.value.x + $cx
        $pressY = [int]$pos.value.y + $cy

        '  {0,-16}   {1},{2}   {3}x{4}   press {5},{6}   {7}' -f `
            $id, $loc.value.x, $loc.value.y, $size.value.width, $size.value.height, `
            $pressX, $pressY, $rect.value
    }

    ''
    'The BoundingRectangle is SCREEN space. If "computed press" does not fall'
    'inside it, this driver is pressing the wrong pixel for that element.'

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='

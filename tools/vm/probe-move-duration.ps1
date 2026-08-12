# Does the reference SPEND a move's duration, and does the window actually move?
#
# Touch_DragAndDrop and Pen_DragAndDrop drag the application's title bar to
# reposition the window:
#
#   pointerMove   -> the title bar
#   pointerDown   -> TouchContact
#   pointerMove   -> +100,+100 from the pointer, duration 1 SECOND
#   pointerUp
#
# Both fail here with "Expected any value except:<{X=160,Y=32}>. Actual:
# <{X=160,Y=32}>" — the window did not move at all.
#
# PointerActionRunner deliberately does NOT sleep for a move's duration: it emits
# frames along the path as fast as it can, on the reasoning that sleeping blocks
# the request thread. That is right for a pause and may be wrong for a DRAG,
# because a window manager samples the pointer across its own message loop - a
# hundred frames delivered in a microsecond is not a gesture it can follow.
#
# Two measurements, both drivers:
#   request ms   how long POST /actions takes for a 1000 ms move
#                -> if the reference spends ~1 s and we spend ~0, that is the gap
#   moved        whether the window position actually changed
#
# The second is the one that matters. The first only explains it.
#
# THE ORIGIN USES THE W3C ELEMENT KEY. A first version sent {"ELEMENT": id},
# which this driver accepts and WinAppDriver answered with 400 - leaving no
# reference measurement at all. Selenium sends the long uuid key, so that is what
# a probe comparing the two has to send.

$ErrorActionPreference = 'Continue'
$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

function MeasureDriver($driverName, $exePath) {
    "=========================================================="
    "  $driverName"
    "=========================================================="

    Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process Calculator -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    $srv = Start-Process -FilePath $exePath -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    try {
        $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
        if (-not $session) { "  no session"; return }
        Start-Sleep -Seconds 2

        $before = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
        "  window before : x=$($before.value.x) y=$($before.value.y)"

        # The title bar, so the drag repositions the window exactly as the suite
        # does it.
        $title = $null
        foreach ($id in 'AppName','AppNameTitle','TitleBar') {
            try {
                $title = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT
                if ($title) { "  title bar via '$id'"; break }
            } catch { }
        }
        if (-not $title) { "  ABORT: no title bar element"; return }

        $actions = @"
{"actions":[{"type":"pointer","id":"finger1","parameters":{"pointerType":"touch"},"actions":[
{"type":"pointerMove","duration":0,"origin":{"element-6066-11e4-a52e-4f735466cecf":"$title"},"x":0,"y":0},
{"type":"pointerDown","button":0},
{"type":"pointerMove","duration":1000,"origin":"pointer","x":100,"y":100},
{"type":"pointerUp","button":0}]}]}
"@

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try { PostRaw "/session/$session/actions" $actions | Out-Null } catch { "  actions failed: $($_.Exception.Message)" }
        $sw.Stop()

        Start-Sleep -Seconds 1
        $after = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20

        "  POST /actions : {0} ms  (the move asked for 1000 ms)" -f [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        "  window after  : x=$($after.value.x) y=$($after.value.y)"
        "  MOVED         : {0}" -f $(if ($before.value.x -ne $after.value.x -or $before.value.y -ne $after.value.y) { "YES" } else { "NO" })

        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
    }
    finally {
        Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
        Get-Process Calculator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

MeasureDriver 'WinAppDriver 1.2.1 (the reference)' 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
MeasureDriver 'WindowsDriverCore (ours)'           'C:\baseline\host\WindowsDriverCore.exe'

'=== probe complete ==='

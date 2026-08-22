# Is WinAppDriver's ~180 ms per touch phase DELIBERATE, or is it just slow?
#
# MEASURED already: the reference spends 199.7 / 169.8 / 182.4 ms on
# /touch/down, /touch/move and /touch/up, and the window moves. This driver
# spends 8.6 / 6.9 / 1.8 ms and it does not.
#
# THAT EVEN SPREAD ALREADY DISPROVES ONE THEORY. If the reference recorded the
# down and the move as intent and replayed the whole gesture on the up, the cost
# would be lopsided - two cheap requests and one expensive one. It is not. The
# reference does real work in every phase.
#
# WHAT IS STILL UNKNOWN is whether 180 ms is SPECIAL. WinAppDriver is slow at
# everything - a find costs ~1070 ms against ~33 ms here - so a touch phase
# costing 180 ms may be nothing more than its ordinary overhead.
#
# So this times requests that inject NOTHING and compares:
#
#   baseline ~180 ms  -> the touch phases are ordinary, the reference is simply
#                        slow, and its drag works for some other reason.
#   baseline ~5 ms    -> the reference is SPENDING that time on touch
#                        deliberately, and holding each phase is the behaviour to
#                        match rather than a side effect to ignore.
#
# The distinction decides whether "be slower" is a real fix or a superstition.

$ErrorActionPreference = 'Continue'

if (-not (Test-Path 'C:\baseline')) {
    throw 'REFUSED: guest only (C:\baseline not found).'
}

$base = 'http://127.0.0.1:4723'
$ALARMS = 'Microsoft.WindowsAlarms_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

function Timed($label, $block) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = 'ok'
    try { & $block | Out-Null } catch { $ok = 'failed' }
    $sw.Stop()
    '  {0,-34} {1,8:N1} ms   {2}' -f $label, $sw.Elapsed.TotalMilliseconds, $ok
}

Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$srv = Start-Process -FilePath 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe' -PassThru
for ($i = 0; $i -lt 40; $i++) {
    try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
}

try {
    $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$ALARMS`"}}").sessionId
    if (-not $session) { throw 'ABORT: no session' }
    Start-Sleep -Seconds 2

    'WinAppDriver 1.2.1 - cost of requests that inject NOTHING:'
    ''
    Timed 'GET /status' { Invoke-RestMethod -Uri "$base/status" -TimeoutSec 20 }
    Timed 'GET /window_handle' { Invoke-RestMethod -Uri "$base/session/$session/window_handle" -TimeoutSec 20 }
    Timed 'GET /window/current/position' { Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20 }
    Timed 'GET /orientation' { Invoke-RestMethod -Uri "$base/session/$session/orientation" -TimeoutSec 20 }
    Timed 'POST /timeouts' { PostRaw "/session/$session/timeouts" '{"type":"implicit","ms":0}' }

    ''
    'and the touch phases again, for comparison in the SAME session:'
    ''
    $title = $null
    foreach ($id in 'AppName','AppNameTitle','TitleBar') {
        try {
            $title = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT
            if ($title) { break }
        } catch { }
    }
    if ($title) {
        $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$title/location" -TimeoutSec 20
        $x = [int]$loc.value.x + 30
        $y = [int]$loc.value.y + 8
        Timed '/touch/down' { PostRaw "/session/$session/touch/down" "{`"x`":$x,`"y`":$y}" }
        Timed '/touch/move' { PostRaw "/session/$session/touch/move" "{`"x`":$($x + 60),`"y`":$($y + 60)}" }
        Timed '/touch/up'   { PostRaw "/session/$session/touch/up"   "{`"x`":$($x + 60),`"y`":$($y + 60)}" }
    }
    else { '  (no title bar element, touch phases skipped)' }

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='

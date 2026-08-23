# DOES A FRAME FIT IN THE SLOT THE PACING GIVES IT?
#
# Pen_Scroll_Vertical passed on the guest 13 runs in a row and failed on the
# 14th, the run that made a move's frame COUNT follow its duration. Before:
# 10 frames of 20 px, 50 ms apart. After: 62 frames of ~3 px, 8 ms apart. The
# velocity is identical on paper - 200 px in 500 ms either way - so on paper
# nothing changed.
#
# UNLESS INJECTING A FRAME COSTS MORE THAN 8 MS. Then 62 frames cannot fit in
# 500 ms, the move overruns, and the velocity the application sees drops below
# whatever a LoopingSelector treats as a scroll. That is the same "a slower
# gesture is a different signal" argument that motivated the frame-rate change,
# running in the other direction.
#
# THE MEASUREMENT: post one /actions move with a stated duration and compare the
# ELAPSED time against it. A move that takes about as long as it asked for means
# the frames fit and the hypothesis is refuted. A move that takes much longer
# means they do not, and the overrun IS the mechanism.
#
# Pen and touch are measured separately because they inject through different
# APIs - touch goes through the WinRT InputInjector, pen still through Win32 -
# and there is no reason to assume they cost the same.
#
# THE SHORT DURATION IS THE CONTROL. At 80 ms the frame count is the floor of
# ten, which is the OLD behaviour; at 500 ms it is 62, the new one. If both
# overrun by the same ratio then the frame count is not what changed and this
# probe is measuring the wrong thing. Without that row, an overrun at 500 ms
# alone cannot be attributed to the count.

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

# The apphost's timestamp never moves; the DLL beside it is what a rebuild
# rewrites, and reporting the wrong one once made a stale run look fresh.
$dll = Join-Path (Split-Path $exe) 'WindowsDriverCore.Protocol.dll'
"code:  $((Get-Item $dll).LastWriteTime)"

Get-Process WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process CalculatorApp, Calculator -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$srv = Start-Process -FilePath $exe -PassThru
for ($i = 0; $i -lt 40; $i++) {
    try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
}

try {
    $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
    if (-not $session) { throw 'ABORT: no session' }
    Start-Sleep -Seconds 3

    ''
    '  kind   asked   frames   elapsed   per frame   overrun'
    '  -----  ------  -------  --------  ----------  -------'

    foreach ($kind in 'touch', 'pen') {
        foreach ($askedMs in 80, 500, 1000) {
            # Ten is the floor, so 80 ms is the OLD fixed-count behaviour and the
            # longer rows are the new duration-driven one.
            $frames = [Math]::Max(10, [int]($askedMs / 8))

            # A short move inside the client area. The distance does not matter
            # to the timing and a small one keeps the gesture off the chrome.
            $body = @"
{"actions":[{"type":"pointer","id":"p","parameters":{"pointerType":"$kind"},"actions":[
 {"type":"pointerMove","origin":"viewport","x":120,"y":300},
 {"type":"pointerDown","button":0},
 {"type":"pointerMove","origin":"viewport","x":120,"y":340,"duration":$askedMs},
 {"type":"pointerUp","button":0}]}]}
"@
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $note = ''
            try { PostRaw "/session/$session/actions" $body | Out-Null }
            catch { $note = 'REFUSED' }
            $sw.Stop()

            $elapsed = $sw.Elapsed.TotalMilliseconds
            '  {0,-5}  {1,4} ms  {2,7}  {3,6:N0} ms  {4,7:N2} ms  {5,5:N2}x {6}' -f `
                $kind, $askedMs, $frames, $elapsed, ($elapsed / $frames), ($elapsed / $askedMs), $note

            Start-Sleep -Milliseconds 400
        }
    }

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process CalculatorApp, Calculator -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='
''
'READ IT LIKE THIS:'
'  overrun ~1.0x everywhere      -> frames fit; the frame count is NOT the'
'                                   mechanism and Pen_Scroll_Vertical broke for'
'                                   some other reason.'
'  overrun grows with the count  -> injection costs more than its 8 ms slot, the'
'                                   move runs long, and the velocity the app sees'
'                                   is lower than the one the client asked for.'

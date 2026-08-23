# DOES A /moveto + /click ACTUALLY PRESS THE BUTTON?
#
# MouseClick fails on the guest with "Expected:<8>. Actual:<0>." - the FIRST
# assertion, so the very first click never registers. It is not the desktop
# session the test needs later, which was my first guess and is wrong: that part
# comes twenty lines further down.
#
# Selenium's session.Mouse.Click(element.Coordinates) is two requests:
#
#   POST /moveto  {"element": "<id>"}     move the cursor to the element centre
#   POST /click   {"button": 0}           press wherever the cursor now is
#
# WHAT MAKES THIS WORTH MEASURING RATHER THAN READING. The route computes the
# element centre correctly and returns 200 either way - a mouse click that lands
# on nothing looks identical on the wire to one that works. Only the
# application's own state distinguishes them, so the assertion here is
# Calculator's DISPLAY, never the HTTP status. Two earlier probes in this repo
# asserted status and proved nothing.
#
# THREE CANDIDATES, and the rows are arranged to separate them:
#
#   moveto+click works               -> the guest failure is app-specific, not
#                                       the mouse path. Look at Alarms, not here.
#   only the element click works     -> the coordinate path is broken while the
#                                       UIA path is fine, so it is /moveto or the
#                                       injection, not the find.
#   neither works unless focused     -> the missing piece is the foreground
#                                       raise. UiaElementInteractor has done this
#                                       before every click since the ladder was
#                                       written, and /moveto+/click does not.
#
# THE UNFOCUSED ROW IS THE POINT. It is run LAST so that a failure there cannot
# poison the rows above it.

$ErrorActionPreference = 'Continue'

$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

function Find($session, $id) {
    try { (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT }
    catch { $null }
}

function Display($session) {
    $r = Find $session 'CalculatorResults'
    if (-not $r) { return '(no display)' }
    try {
        ((Invoke-RestMethod -Uri "$base/session/$session/element/$r/text" -TimeoutSec 20).value `
            -replace 'Display is', '').Trim()
    } catch { '(unreadable)' }
}

# NOT named Clear: that is an alias for Clear-Host, so "Clear $session" runs the
# host cmdlet instead of this function and dies on an invalid console handle.
# The whole probe produced zero rows, which is at least a loud failure.
# THE VERDICT IS THE CHANGE, NOT A GUESSED VALUE. The first version of this
# compared the display to "8" and reported "no" for rows reading 88 and 888 -
# every one of which was a SUCCESSFUL press onto a display that had not been
# cleared. It called a working mouse path broken, which is the exact conclusion
# the probe existed to test, arrived at backwards.
function Pressed($session, $before) {
    $after = Display $session
    if ($after -eq $before) { return "$after`tno" }
    return "$after`tPRESSED"
}

$binRoot = Join-Path $PSScriptRoot '..' | Join-Path -ChildPath 'src/WindowsDriverCore.Host/bin'
$exe = Get-ChildItem -Path $binRoot -Filter 'WindowsDriverCore.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'ABORT: no built driver' }
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

    $eight = Find $session 'num8Button'
    if (-not $eight) { throw 'ABORT: num8Button not found' }

    ''
    '  case                              display   verdict'
    '  --------------------------------  --------  -------'

    # 1. THE CONTROL: the element click route, which the suite exercises
    #    elsewhere and which goes through the UIA ladder rather than the mouse.
    $before = Display $session
    try { PostRaw "/session/$session/element/$eight/click" '{}' | Out-Null } catch { }
    Start-Sleep -Milliseconds 600
    '  {0,-32}  {1}' -f 'element click (control)', (Pressed $session $before)

    # 2. THE SUBJECT: moveto + click, exactly as Selenium sends it.
    $before = Display $session
    try { PostRaw "/session/$session/moveto" "{`"element`":`"$eight`"}" | Out-Null } catch { }
    try { PostRaw "/session/$session/click" '{"button":0}' | Out-Null } catch { }
    Start-Sleep -Milliseconds 600
    '  {0,-32}  {1}' -f 'moveto + click', (Pressed $session $before)

    # 3. Same again, to separate "never works" from "works only once".
    $before = Display $session
    try { PostRaw "/session/$session/moveto" "{`"element`":`"$eight`"}" | Out-Null } catch { }
    try { PostRaw "/session/$session/click" '{"button":0}' | Out-Null } catch { }
    Start-Sleep -Milliseconds 600
    '  {0,-32}  {1}' -f 'moveto + click, again', (Pressed $session $before)

    # NO SLEEP. THIS IS THE ROW THAT MATTERS.
    #
    # Every row above waits 600 ms before reading, and the suite does not - it
    # reads the display in the next request. The guest transcript for MouseClick
    # shows the click dispatched to a sensible point and then "drain -> waited
    # 0.8 ms", which is a drain that did not wait for anything, followed 26 ms
    # later by a text read of "0".
    #
    # If this row says "no" while the identical row above says PRESSED, the
    # defect is synchronisation and not the mouse path - the same shape as the
    # SendKeys drain, which trusted a process-idle proxy that answers "is the
    # process idle" rather than "has my input been consumed".
    $before = Display $session
    try { PostRaw "/session/$session/moveto" "{`"element`":`"$eight`"}" | Out-Null } catch { }
    try { PostRaw "/session/$session/click" '{"button":0}' | Out-Null } catch { }
    '  {0,-32}  {1}' -f 'moveto + click, read immediately', (Pressed $session $before)

    # And once more after settling, to show the press was real and merely late
    # rather than lost.
    Start-Sleep -Milliseconds 800
    '  {0,-32}  {1}' -f '   ... the same press, 800ms later', (Pressed $session $before)

    # NO UNFOCUSED ROW. The obvious fourth case is to minimise everything and
    # click again, to test whether the missing foreground raise is the cause.
    # It is not run here: /moveto + /click has no OwnsThePointAt guard, unlike
    # the UIA ladder and the /actions path, so with Calculator minimised the
    # contact lands on whatever occupies those pixels - the owner's desktop.
    # That gap is worth fixing in the driver; it is not worth demonstrating on
    # somebody's machine.

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process CalculatorApp, Calculator -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='

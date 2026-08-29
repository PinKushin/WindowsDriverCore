# DOES A BATCHED DOUBLE CLICK REGISTER AS A DOUBLE CLICK?
#
# MouseDoubleClick fails on the guest at "Assert.IsFalse(maximizeButton.Text
# .Contains("Maximize"))" - it double-clicks the title bar and the window does
# not maximize.
#
# DoubleClick sends all four events in ONE SendInput batch:
#
#   down, up, down, up        same call, same tick count, zero gap
#
# Windows decides a second WM_LBUTTONDOWN is a double click by the time and
# distance from the first, and zero is inside any threshold - so on paper this
# should work. On paper is not a measurement.
#
# THE A/B NEEDS NO CODE CHANGE, which is why it is worth running first. Two
# SEPARATE /click requests have a real gap between them (an HTTP round trip),
# and /doubleclick has none. If the pair maximizes and the batch does not, the
# batching is the defect and the fix is a gap. If neither works, the problem is
# not the gap and the next candidate is where the click lands.
#
# THE ASSERTION IS THE WINDOW STATE, never the HTTP status. A double click that
# lands on nothing returns 200 exactly like one that works - measured twice in
# this repo, both times by probes that asserted status and proved nothing.
#
# The window is restored between rows, because a maximized subject makes every
# later row start from the wrong state.

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

# The Maximize button's NAME is the state: "Maximize" when restored, "Restore"
# when maximized. The suite reads exactly this, so the probe reads it too rather
# than inventing a different instrument.
function MaximizeLabel($session) {
    $b = Find $session 'Maximize'
    if (-not $b) { return '(no button)' }
    try { (Invoke-RestMethod -Uri "$base/session/$session/element/$b/text" -TimeoutSec 20).value }
    catch { '(unreadable)' }
}

function TitleBar($session) {
    foreach ($id in 'AppName', 'TitleBar', 'AppNameTitle') {
        $e = Find $session $id
        if ($e) { return $e }
    }
    return $null
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

    $title = TitleBar $session
    if (-not $title) { throw 'ABORT: no title bar element' }

    ''
    '  case                             before      after       maximized?'
    '  -------------------------------  ----------  ----------  ----------'

    function Row($label, $act) {
        $before = MaximizeLabel $session
        & $act
        Start-Sleep -Milliseconds 900
        $after = MaximizeLabel $session

        # The LABEL CHANGING is the measurement. Comparing to the literal
        # "Restore" would break on a localised guest and read as a failure of
        # the click rather than of the probe.
        $changed = if ($after -ne $before) { 'YES' } else { 'no' }
        '  {0,-31}  {1,-10}  {2,-10}  {3}' -f $label, $before, $after, $changed

        # Back to restored, whatever happened, so the next row starts clean.
        if ($after -ne 'Maximize') {
            $b = Find $session 'Maximize'
            if ($b) { try { PostRaw "/session/$session/element/$b/click" '{}' | Out-Null } catch { } }
            Start-Sleep -Milliseconds 900
        }
    }

    # 1. THE SUBJECT: one batched /doubleclick, exactly what the suite sends.
    Row 'doubleclick (one batch)' {
        try { PostRaw "/session/$session/moveto" "{`"element`":`"$title`"}" | Out-Null } catch { }
        try { PostRaw "/session/$session/doubleclick" '{}' | Out-Null } catch { }
    }

    # 2. THE A/B: two separate clicks, which carry a real gap between them.
    Row 'two separate /click requests' {
        try { PostRaw "/session/$session/moveto" "{`"element`":`"$title`"}" | Out-Null } catch { }
        try { PostRaw "/session/$session/click" '{"button":0}' | Out-Null } catch { }
        try { PostRaw "/session/$session/click" '{"button":0}' | Out-Null } catch { }
    }

    # 3. THE CONTROL. A single click must NOT maximize. Without this, a row
    #    reading YES proves only that SOMETHING changed the window - and the
    #    restore step between rows is itself a click on this very button.
    Row 'a single click (control)' {
        try { PostRaw "/session/$session/moveto" "{`"element`":`"$title`"}" | Out-Null } catch { }
        try { PostRaw "/session/$session/click" '{"button":0}' | Out-Null } catch { }
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
'  batch no, pair YES   -> the batching is the defect; the two clicks need a gap.'
'  both YES             -> double click works here and the guest failure is'
'                          elsewhere - look at where the click lands.'
'  both no              -> not the gap. Next candidate is the coordinate or the'
'                          caption area, not the timing.'

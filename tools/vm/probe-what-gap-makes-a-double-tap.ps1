<#
    What separation does Windows need between two taps to see a double tap?

    TouchDoubleTap fails at Assert.IsFalse - the double tap on Calculator's title
    bar does not maximize the window. The mouse equivalent, MouseDoubleClick, was
    a surviving context menu and is fixed; this is a different subject and shares
    none of that.

    /touch/doubleclick today sends two taps BACK TO BACK:

        runner.Tap(x, y, 30ms) ?? runner.Tap(x, y, 30ms)

    Each is down, ~30 ms of update frames, up. The gap between the first lift and
    the second press is loop overhead - effectively nothing. Windows pairs two
    contacts into a double tap only inside the double-click TIME and RECTANGLE,
    and the position is identical here, which leaves timing. Nothing says the
    stack cannot also coalesce two contacts that arrive too CLOSE together, and
    that is the hypothesis: we are too fast rather than too slow.

    DRIVEN FROM THE CLIENT, through /actions, so the separation is a parameter
    rather than a rebuild. Each case is a W3C touch sequence: move, down, hold,
    up, PAUSE, down, hold, up.

    THE CASES:
      /touch/doubleclick   the route as it stands today, the baseline
      gap 0, 40, 80, 160   the same gesture with a measured separation
      a single tap         the CONTROL - it must NOT maximize, or the probe
                           cannot tell a double tap from any tap at all

    Verdict is the CHANGE in the Maximize button's own label, and the window is
    forced to a restored state before every case. An earlier probe in this
    repository computed an absolute instead and reported the exact opposite of
    its own raw values.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$driver = 'C:\baseline\host\WindowsDriverCore.exe'
if (-not (Test-Path $driver)) { "ABORT: no driver at $driver"; return }

Get-Process WindowsDriverCore, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$server = Start-Process -FilePath $driver -PassThru -WindowStyle Minimized

function Wire([string] $method, [string] $path, [string] $body) {
    $a = @{ Uri = "http://127.0.0.1:4723$path"; Method = $method; TimeoutSec = 30; UseBasicParsing = $true }
    if ($method -ne 'GET') { $a.Body = $body; $a.ContentType = 'application/json' }
    try { (Invoke-WebRequest @a).Content }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return $null }
        $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
        try { $sr.ReadToEnd() } finally { $sr.Dispose() }
    }
}

function Value([string] $json) { if ($json) { ($json | ConvertFrom-Json).value } else { $null } }

function Find([string] $session, [string] $value) {
    (Value (Wire 'POST' "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$value`"}")).ELEMENT
}

# A W3C touch sequence with an explicit separation between the two taps.
function TapTwice([string] $session, [int] $x, [int] $y, [int] $gap, [bool] $twice) {
    $second = if ($twice) {
        @"
,{"type":"pause","duration":$gap},
 {"type":"pointerDown","button":0},
 {"type":"pause","duration":30},
 {"type":"pointerUp","button":0}
"@
    } else { '' }

    $body = @"
{"actions":[{"type":"pointer","id":"finger","parameters":{"pointerType":"touch"},"actions":[
 {"type":"pointerMove","duration":0,"x":$x,"y":$y},
 {"type":"pointerDown","button":0},
 {"type":"pause","duration":30},
 {"type":"pointerUp","button":0}$second
]}]}
"@
    Wire 'POST' "/session/$session/actions" $body
}

try {
    $ready = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $ready = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $ready) { 'ABORT: driver never answered /status'; return }

    "{0,-26} {1,-10} {2}" -f 'case', 'maximized', 'note'
    "{0,-26} {1,-10} {2}" -f '-------------------------', '---------', '----'

    foreach ($case in 'route /touch/doubleclick', 'actions gap 0', 'actions gap 40',
                      'actions gap 80', 'actions gap 160', 'a single tap (CONTROL)') {

        $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
        if (-not $session) { "ABORT: no session for '$case'"; return }

        $maxId = Find $session 'Maximize'
        $titleId = Find $session 'AppName'
        if (-not $maxId -or -not $titleId) { "ABORT: missing an element for '$case'"; return }

        # FORCED, not assumed. 'Maximize Calculator' means the window is restored.
        $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
        if ($state -and -not "$state".Contains('Maximize')) {
            Wire 'POST' "/session/$session/element/$maxId/click" '{}' | Out-Null
            Start-Sleep -Seconds 1
            $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
        }
        if ($state -and -not "$state".Contains('Maximize')) {
            "ABORT: could not restore the window (button says '$state')"; return
        }

        $before = $state

        # The point the suite taps: the AppName element's centre.
        $loc = Value (Wire 'GET' "/session/$session/element/$titleId/location" '{}')
        $size = Value (Wire 'GET' "/session/$session/element/$titleId/size" '{}')
        $x = [int]$loc.x + [int]([int]$size.width / 2)
        $y = [int]$loc.y + [int]([int]$size.height / 2)

        $note = ''
        switch -Wildcard ($case) {
            'route *' {
                Wire 'POST' "/session/$session/moveto" "{`"element`":`"$titleId`"}" | Out-Null
                $answer = Wire 'POST' "/session/$session/touch/doubleclick" "{`"element`":`"$titleId`"}"
                if (-not $answer) { $note = 'no answer from the route' }
            }
            'a single tap*' {
                $answer = TapTwice $session $x $y 0 $false
                if (-not $answer) { $note = 'no answer from /actions' }
            }
            default {
                $gap = [int]($case -replace '\D', '')
                $answer = TapTwice $session $x $y $gap $true
                if (-not $answer) { $note = 'no answer from /actions' }
            }
        }

        Start-Sleep -Seconds 2
        $after = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')

        "{0,-26} {1,-10} {2}" -f $case, $(if ($after -ne $before) { 'YES' } else { 'no' }), $note

        Wire 'DELETE' "/session/$session" '{}' | Out-Null
        Start-Sleep -Milliseconds 600
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
}

<#
    Why does /touch/doubleclick work in a probe and fail in the suite?

    MEASURED YESTERDAY: /touch/doubleclick DOES maximize Calculator - the route
    is not broken. But that probe sent a /moveto first, and the suite does not:
    Selenium's touchScreen.DoubleTap(coordinates) sends the touch command alone.

    /moveto is also the route that gained BringToForeground earlier today, when
    the same mechanism turned out to be MouseClick's defect: a click into a
    background window is consumed by the activation it causes.

    AND NOTHING ELSE IN THE TOUCH PATH RAISES. Counting BringToForeground call
    sites across the routing layer:

        KeyboardRoutes 1   MouseRoutes 2   NavigationRoutes 1   ScreenshotRoutes 2
        TouchRoutes 0      ActionRoutes 0  PointerActionRunner 0  WindowRoutes 0

    Four of the seven remaining failures are touch or pen. That is a coherent
    single story, which is exactly when it needs testing rather than believing.

    /moveto DOES TWO THINGS, so "with moveto it works" does not name which:
    it raises the window AND it positions the mouse pointer. This separates them.

      1. the route alone              what the suite sends
      2. moveto to the TITLE, route   what the earlier probe did - raise AND
                                      pointer over the target
      3. moveto to the CLEAR BUTTON,  raises, but leaves the pointer somewhere
         then route on the title      ELSE. If this works, the RAISE is what
                                      matters and the pointer position is not.
      4. a single tap (CONTROL)       must not maximize

    Case 3 is the discriminating one. Without it, cases 1 and 2 differ by two
    variables and the result names neither.
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

try {
    $ready = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $ready = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $ready) { 'ABORT: driver never answered /status'; return }

    "{0,-34} {1,-10} {2,-6} {3}" -f 'case', 'maximized', 'round', 'restoring the window first'
    "{0,-34} {1,-10} {2,-6} {3}" -f '---------------------------------', '---------', '-----', '--------------------------'

    foreach ($round in 1, 2) {
        foreach ($case in 'route alone (WHAT THE SUITE SENDS)',
                          'moveto title, then route',
                          'moveto CLEAR button, then route',
                          'a single tap (CONTROL)') {

            $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
            if (-not $session) { "ABORT: no session for '$case'"; return }

            $maxId = Find $session 'Maximize'
            $titleId = Find $session 'AppName'
            $clearId = Find $session 'clearButton'
            if (-not $maxId -or -not $titleId -or -not $clearId) { "ABORT: missing an element for '$case'"; return }

            # FORCED. 'Maximize Calculator' means the window is restored.
            #
            # THE RESTORE IS ITSELF A MEASUREMENT, and it used to abort the run.
            # The suite's own guard is exactly this - "if the button does not say
            # Maximize, click it" - so a click on the Restore button that does
            # nothing is a finding, not a broken fixture. It is reported in the
            # note column and the case still runs, using the route already shown
            # to work as the fallback.
            $restore = ''
            $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')

            if ($state -and -not "$state".Contains('Maximize')) {
                Wire 'POST' "/session/$session/element/$maxId/click" '{}' | Out-Null

                foreach ($wait in 1..12) {
                    Start-Sleep -Milliseconds 250
                    $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
                    if ($state -and "$state".Contains('Maximize')) { break }
                }

                if ($state -and -not "$state".Contains('Maximize')) {
                    $restore = 'BUTTON CLICK DID NOT RESTORE; used the touch route'
                    Wire 'POST' "/session/$session/touch/doubleclick" "{`"element`":`"$titleId`"}" | Out-Null
                    Start-Sleep -Seconds 2
                    $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
                }
                else {
                    $restore = "button restored in $($wait * 250) ms"
                }
            }

            if ($state -and -not "$state".Contains('Maximize')) {
                "ABORT: nothing could restore the window (button says '$state')"; return
            }
            $before = $state

            switch -Wildcard ($case) {
                'route alone*' {
                    Wire 'POST' "/session/$session/touch/doubleclick" "{`"element`":`"$titleId`"}" | Out-Null
                }
                'moveto title*' {
                    Wire 'POST' "/session/$session/moveto" "{`"element`":`"$titleId`"}" | Out-Null
                    Wire 'POST' "/session/$session/touch/doubleclick" "{`"element`":`"$titleId`"}" | Out-Null
                }
                'moveto CLEAR*' {
                    Wire 'POST' "/session/$session/moveto" "{`"element`":`"$clearId`"}" | Out-Null
                    Wire 'POST' "/session/$session/touch/doubleclick" "{`"element`":`"$titleId`"}" | Out-Null
                }
                default {
                    Wire 'POST' "/session/$session/touch/click" "{`"element`":`"$titleId`"}" | Out-Null
                }
            }

            Start-Sleep -Seconds 2
            $after = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')

            "{0,-34} {1,-10} {2,-6} {3}" -f `
                $case, $(if ($after -ne $before) { 'YES' } else { 'no' }), $round, $restore

            Wire 'DELETE' "/session/$session" '{}' | Out-Null
            Start-Sleep -Milliseconds 600
        }
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
}

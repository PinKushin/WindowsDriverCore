<#
    Does an element click dismiss an open context menu?

    THE SUITE'S OWN SEQUENCE, from Tests/WebDriverAPI/Mouse.cs. MouseClick
    right-clicks the title bar to raise the system menu, asserts it contains
    Minimize, and then dismisses it:

        clearButton.Click(); // Dismiss the context menu

    WinAppDriver serves that as a PHYSICAL CLICK at the element's point, and a
    click outside a popup menu closes it. This driver serves it through the
    click ladder, which prefers InvokePattern - deliberately, and it is the
    project's headline capability claim (docs/CLICK-SEMANTICS.md). An Invoke
    sends no mouse input at all, so it has nothing to dismiss a menu WITH.

    IF THE MENU SURVIVES, the next two tests in the class both act on the title
    bar underneath it, and both fail exactly as measured on the guest:

      MouseDoubleClick  line 107  IsFalse  - the double click never maximizes
      MouseDownMoveUp   line 137  IsTrue   - the window moves, but not DOWN

    The second is the interesting one and the reason this is worth measuring
    rather than assuming: line 136 PASSES, so the window really does move. A
    menu item pressed at the down point and released at the up point is
    ACTIVATED, and activating Maximize moves the window up - a changed position
    in the wrong direction, which is that pair of results precisely.

    THE A/B:
      A  dismiss with POST /element/{id}/click   - what the suite sends
      B  dismiss with /moveto + /click           - a real mouse click, the CONTROL

    Both then ask a desktop session whether the menu is still there, then double
    click the title bar and report whether the window maximized. Case B must
    dismiss and must maximize, or the probe cannot tell a driver defect from a
    guest that behaves this way no matter what.

    Each case runs TWICE. One observation of a race is not a measurement.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$driver = 'C:\baseline\host\WindowsDriverCore.exe'
if (-not (Test-Path $driver)) { "ABORT: no driver at $driver"; return }

Get-Process WindowsDriverCore, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$server = Start-Process -FilePath $driver -PassThru -WindowStyle Minimized

function Wire([string] $method, [string] $path, [string] $body) {
    $a = @{ Uri = "http://127.0.0.1:4723$path"; Method = $method; TimeoutSec = 20; UseBasicParsing = $true }
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

function Find([string] $session, [string] $using, [string] $value) {
    (Value (Wire 'POST' "/session/$session/element" "{`"using`":`"$using`",`"value`":`"$value`"}")).ELEMENT
}

# IS THE MENU STILL UP? Asked exactly the way the suite asks it - a desktop
# session, because the popup is parented on the desktop rather than on the app.
function MenuIsOpen {
    $desktop = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Root"}}' | ConvertFrom-Json).sessionId
    if (-not $desktop) { return 'no desktop session' }
    try {
        $system = Find $desktop 'name' 'System'
        if (-not $system) { return 'no' }
        $minimize = (Value (Wire 'POST' "/session/$desktop/element/$system/element" '{"using":"name","value":"Minimize"}')).ELEMENT
        if ($minimize) { 'YES' } else { 'partly (System found, Minimize not)' }
    }
    finally { Wire 'DELETE' "/session/$desktop" '{}' | Out-Null }
}

try {
    $ready = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $ready = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $ready) { 'ABORT: driver never answered /status'; return }

    "{0,-22} {1,-10} {2,-14} {3}" -f 'dismissed by', 'menu open', 'then maximized', 'note'
    "{0,-22} {1,-10} {2,-14} {3}" -f '--------------------', '---------', '-------------', '----'

    foreach ($round in 1, 2) {
        foreach ($case in 'element click (Invoke)', 'moveto + click (REAL)') {

            $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
            if (-not $session) { "ABORT: no session for '$case'"; return }

            $maxId = Find $session 'accessibility id' 'Maximize'
            $titleId = Find $session 'accessibility id' 'AppName'
            $clearId = Find $session 'accessibility id' 'clearButton'
            if (-not $maxId -or -not $titleId -or -not $clearId) { "ABORT: missing an element for '$case'"; return }

            # THE STARTING STATE IS FORCED. 'Maximize Calculator' means restored.
            $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
            if ($state -and -not "$state".Contains('Maximize')) {
                Wire 'POST' "/session/$session/element/$maxId/click" '{}' | Out-Null
                Start-Sleep -Seconds 1
                $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
            }
            if ($state -and -not "$state".Contains('Maximize')) {
                "ABORT: could not restore the window (button says '$state')"; return
            }

            # MouseClick's tail: right click the title bar to raise the menu.
            Wire 'POST' "/session/$session/moveto" "{`"element`":`"$titleId`"}" | Out-Null
            Wire 'POST' "/session/$session/click" '{"button":2}' | Out-Null
            Start-Sleep -Milliseconds 1500

            $raised = MenuIsOpen
            if ($raised -ne 'YES') {
                "ABORT: the right click did not raise the menu ($raised) - nothing to dismiss"
                Wire 'DELETE' "/session/$session" '{}' | Out-Null
                return
            }

            switch ($case) {
                'element click (Invoke)' {
                    Wire 'POST' "/session/$session/element/$clearId/click" '{}' | Out-Null
                }
                default {
                    Wire 'POST' "/session/$session/moveto" "{`"element`":`"$clearId`"}" | Out-Null
                    Wire 'POST' "/session/$session/click" '{}' | Out-Null
                }
            }
            Start-Sleep -Milliseconds 800

            $stillOpen = MenuIsOpen

            # Now MouseDoubleClick, unchanged.
            $before = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
            Wire 'POST' "/session/$session/moveto" "{`"element`":`"$titleId`"}" | Out-Null
            Wire 'POST' "/session/$session/doubleclick" '{}' | Out-Null
            Start-Sleep -Seconds 2
            $after = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')

            "{0,-22} {1,-10} {2,-14} {3}" -f `
                $case, $stillOpen, $(if ($after -ne $before) { 'YES' } else { 'no' }), "round $round"

            Wire 'DELETE' "/session/$session" '{}' | Out-Null
            Start-Sleep -Milliseconds 600
        }
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
}

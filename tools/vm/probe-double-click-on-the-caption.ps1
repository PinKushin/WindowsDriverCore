<#
    Why does a double click on the caption not maximize, on the GUEST?

    THREE HYPOTHESES ARE ALREADY DEAD.
      - "too fast": refuted on the host - one batch maximizes, two separate
        /click requests maximize, a single click does not (the control).
      - "wrong point": refuted on the guest - WM_NCHITTEST at the exact point
        the suite clicks, (47,24), answers HTCAPTION.
      - "read race": the test sleeps a full second before asserting.

    WHAT IS LEFT is that the host and the guest disagree, and they run different
    windows: the host's Calculator is WinUI with its own title bar, the guest's
    is a UWP hosted in an ApplicationFrameWindow. DefWindowProc handles a caption
    press by entering a NESTED MODAL MOVE LOOP - already documented in
    LIMITATIONS - which would consume the rest of a batched down/up/down/up
    before Windows ever scores it as a double click.

    THE A/B, run on the guest against the real subject:
      1. one batch of four events, which is what DoubleClick sends today
      2. two separate /click requests, which the host measured as equivalent
      3. a single click, the CONTROL - it must NOT maximize, or the probe cannot
         tell a double click from any click at all

    Verdict is read from the Maximize button's own label, not from an HTTP
    status: "Maximize" before, "Restore" after. A 200 means the input was
    dispatched and says nothing about what the window did.
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

try {
    $ready = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $ready = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $ready) { 'ABORT: driver never answered /status'; return }

    foreach ($case in 'one batch (DoubleClick)', 'two separate clicks', 'a single click (CONTROL)') {

        # A FRESH SESSION PER CASE, so a window left maximized by the previous
        # one cannot make the next look like a success.
        $session = (Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}' | ConvertFrom-Json).sessionId
        if (-not $session) { "ABORT: no session for '$case'"; return }

        $maxId = (Value (Wire 'POST' "/session/$session/element" '{"using":"accessibility id","value":"Maximize"}')).ELEMENT
        $titleId = (Value (Wire 'POST' "/session/$session/element" '{"using":"accessibility id","value":"AppName"}')).ELEMENT

        if (-not $maxId -or -not $titleId) { "ABORT: could not find Maximize/AppName for '$case'"; return }

        # THE STARTING STATE IS FORCED, not assumed. The first run of this probe
        # found Calculator already maximized and its verdict column - written for
        # a restored start - reported the exact opposite of what the raw
        # before/after values showed. The suite maximizes FROM RESTORED, so that
        # is the direction to test, and it has to be arranged rather than hoped
        # for.
        $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
        if ($state -and -not "$state".Contains('Maximize')) {
            Wire 'POST' "/session/$session/moveto" "{`"element`":`"$titleId`"}" | Out-Null
            Wire 'POST' "/session/$session/doubleclick" '{}' | Out-Null
            Start-Sleep -Seconds 2
            $state = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')
        }

        if ($state -and -not "$state".Contains('Maximize')) {
            "ABORT: could not get the window into a restored state (button says '$state')"
            return
        }

        $before = $state

        Wire 'POST' "/session/$session/moveto" "{`"element`":`"$titleId`"}" | Out-Null

        switch ($case) {
            'one batch (DoubleClick)' { Wire 'POST' "/session/$session/doubleclick" '{}' | Out-Null }
            'two separate clicks'     {
                Wire 'POST' "/session/$session/click" '{}' | Out-Null
                Wire 'POST' "/session/$session/click" '{}' | Out-Null
            }
            default                   { Wire 'POST' "/session/$session/click" '{}' | Out-Null }
        }

        # The suite sleeps a second here, and the window animates. Matching it so
        # the probe measures the same thing the suite does.
        Start-Sleep -Seconds 2

        $after = Value (Wire 'GET' "/session/$session/element/$maxId/text" '{}')

        # THE CHANGE, never a guessed absolute. 'Maximize Calculator' means the
        # window is RESTORED (clicking would maximize it); 'Restore Calculator'
        # means it is maximized. Starting restored, success is the label moving
        # from the first to the second.
        "{0,-26} before='{1}'  after='{2}'  maximized={3}" -f `
            $case, $before, $after, $(if ($after -ne $before) { 'YES' } else { 'no' })

        # Restored by the same route that maximized it, so the next case starts
        # from a known state whether or not this one worked.
        if ($after -ne $before) {
            Wire 'POST' "/session/$session/doubleclick" '{}' | Out-Null
            Start-Sleep -Seconds 1
        }

        Wire 'DELETE' "/session/$session" '{}' | Out-Null
        Start-Sleep -Milliseconds 500
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
}

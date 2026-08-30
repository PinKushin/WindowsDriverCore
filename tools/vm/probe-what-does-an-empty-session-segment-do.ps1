<#
    What does the reference do with GET /session//title ?

    THE LAST DIAGNOSED BACKLOG TEST. MiscellaneousSessionError_StaleSessionId
    quits a session and then reads Title. Selenium 3.8 clears its session id on
    Quit(), so the URL it builds has an EMPTY segment:

        GET /session//title

    and the test requires the message to start with:

        No active session with ID title

    "title" as the session ID. So the reference is not matching
    /session/:sessionId/title with an empty id - it is dropping the empty
    segment, leaving /session/title, and treating `title` as a session name. We
    answer 404 status 9 instead.

    WHAT IS NOT YET KNOWN, and the reason for a probe rather than an assumption:
    whether the reference serves GET /session/:sessionId at all. JWP defines it
    (retrieve the session's capabilities) but WinAppDriver's own endpoint table
    does not list it, and its unrecognised-path answer is 404. Either it serves
    the route and the empty segment collapses into it, or its session lookup runs
    before its command lookup. The two are different fixes.

    RAW SOCKETS, NOT Invoke-WebRequest. System.Uri canonicalises a path, and a
    client that collapses `//` before sending would have this probe measuring
    itself rather than the server. The request line is written byte for byte.

    IT CHANGES NOTHING - four GETs, one session created and quit.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$reference = 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
if (-not (Test-Path $reference)) { "ABORT: no WinAppDriver at $reference"; return }

Get-Process WinAppDriver, WindowsDriverCore, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$server = Start-Process -FilePath $reference -PassThru -WindowStyle Minimized

# THE REQUEST LINE IS WRITTEN VERBATIM. Nothing between this and the socket gets
# to normalise the path, which is the entire point.
function Raw([string] $method, [string] $path, [string] $body) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', 4723)
        $stream = $client.GetStream()

        $head = "$method $path HTTP/1.1`r`nHost: 127.0.0.1:4723`r`nConnection: close`r`n"
        if ($body) {
            $head += "Content-Type: application/json`r`nContent-Length: $($body.Length)`r`n"
        }
        $head += "`r`n"

        $bytes = [System.Text.Encoding]::ASCII.GetBytes($head + $body)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()

        $reader = New-Object System.IO.StreamReader($stream)
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $client.Dispose() }
}

function StatusLine([string] $response) {
    if (-not $response) { return '(no response)' }
    ($response -split "`r`n")[0]
}

function BodyOf([string] $response) {
    if (-not $response) { return '' }
    $split = $response -split "`r`n`r`n", 2
    if ($split.Count -lt 2) { return '' }
    # Chunked encoding wraps the body in length lines; strip anything that is
    # not part of the JSON rather than pretending it is absent.
    $text = $split[1]
    $start = $text.IndexOf('{')
    if ($start -lt 0) { return ($text -replace "`r?`n", ' ').Trim() }
    $text.Substring($start).Trim() -replace "`r?`n", ' '
}

function Report([string] $label, [string] $method, [string] $path, [string] $body) {
    $response = Raw $method $path $body
    ''
    "$label"
    "  $method $path"
    "  -> $(StatusLine $response)"
    "  -> $(BodyOf $response)"
}

try {
    $up = $false
    foreach ($i in 1..40) {
        try { if (Raw 'GET' '/status' '') { $up = $true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $up) { 'ABORT: the reference never answered /status'; return }

    'reference: WinAppDriver ' + (Get-Item $reference).VersionInfo.FileVersion

    Report 'an unknown session, by the JWP get-capabilities route' `
        'GET' '/session/never-existed' ''

    Report 'THE ONE SELENIUM SENDS after Quit()' `
        'GET' '/session//title' ''

    Report 'an unrecognised command under a bad session, for contrast' `
        'GET' '/session/never-existed/not-a-command' ''

    # A LIVE session, so "does it serve get-capabilities" is answerable rather
    # than confounded with "the session did not exist".
    $created = Raw 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}'
    $body = BodyOf $created
    $session = $null
    if ($body -match '"sessionId"\s*:\s*"([^"]+)"') { $session = $Matches[1] }

    if (-not $session) {
        ''
        "could not create a session for the live case: $(StatusLine $created) $body"
    }
    else {
        Report 'a LIVE session, same route' 'GET' "/session/$session" ''
        Report 'a LIVE session, empty segment' 'GET' "/session//title" ''
        Raw 'DELETE' "/session/$session" '' | Out-Null
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
}

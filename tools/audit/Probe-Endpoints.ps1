<#
.SYNOPSIS
    Asks a running driver which endpoints it actually serves.

.DESCRIPTION
    The by-route lens of the protocol audit, run against the SERVER rather than
    against the source text.

    Pass 4 of the audit used a grep for literal route paths and reported sixteen
    endpoints missing when three were. Nine of the false positives are registered
    through helpers - MapRead(app, "text", ...), MapAction, MapDrag, MapGeometry -
    where the path exists only as an argument, and no grep for "/text" can see it.

    A 404 from the driver is a measurement. A grep miss is a hypothesis.

    Pass 6 then found six real gaps this way, all of them in WinAppDriver's OWN
    documented API rather than in the W3C spec - the audit had been looking
    outward at W3C and had not checked the API this project exists to implement.

.PARAMETER Port
    Where the driver is listening. Start it yourself; this script does not,
    deliberately - it must measure the binary you meant to measure, and starting
    one here would hide a stale build.

.PARAMETER ApiList
    WinAppDriver's SupportedAPIs.md. Its table is the reference surface.

.NOTES
    THREE OF ITS ROWS ARE WRONG and will always report missing:
    /element/:id/element and /element/:id/elements are listed as GET and are
    POST, and /element/:id/equals omits the trailing /:other. Confirmed served
    under the correct verb and path. They are not filtered out here, because a
    filter is how a real gap gets hidden behind a known-typo exemption - the
    verdict text says which is which instead.

    The CONTROL is not optional. A probe that classifies by matching
    "Command not recognized" reports everything as served the moment that
    wording changes, and a clean sweep and a broken probe look identical. Three
    filters in this project's history could only report success.
#>
[CmdletBinding()]
param(
    [int] $Port = 4723,

    [string] $ApiList =
        "$PSScriptRoot\..\..\..\WinAppDriver\docs\SupportedAPIs.md"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = "http://127.0.0.1:$Port"

function Test-Served {
    param([string] $Method, [string] $Path)

    try {
        $response = Invoke-WebRequest -Uri "$root$Path" -Method $Method `
            -Body '{}' -ContentType 'application/json' `
            -TimeoutSec 5 -UseBasicParsing -SkipHttpErrorCheck
        $body = $response.Content
    }
    catch {
        # A transport failure is not an answer about the route. Reported as
        # such rather than counted as missing, which would blame the driver for
        # a server that is not running.
        return [pscustomobject]@{ Served = $null; Detail = $_.Exception.Message }
    }

    return [pscustomobject]@{
        Served = -not ($body -match 'Command not recognized')
        Detail = ''
    }
}

# THE CONTROL, FIRST. If the probe cannot detect an absent route, every verdict
# after this line is meaningless and the run must not continue.
$control = Test-Served -Method 'POST' -Path '/session/fake-id/definitely-not-a-route'

if ($null -eq $control.Served) {
    throw "No driver answering on $root - start it first. ($($control.Detail))"
}

if ($control.Served) {
    throw 'CONTROL FAILED: an invented route reported as served. The probe cannot detect an absence, so this run proves nothing.'
}

Write-Host 'control ok: an invented route is detected as missing' -ForegroundColor DarkGray

if (-not (Test-Path -LiteralPath $ApiList)) {
    throw "WinAppDriver's SupportedAPIs.md not found at $ApiList"
}

# | GET     | [/status  ](./../Tests/WebDriverAPI/Status.cs)  |
# @() so no matches yields an EMPTY ARRAY rather than $null. Without it the
# count assertion below dies on "the property 'Count' cannot be found", which
# still fails - but says nothing about the table having changed, which is the
# thing the reader needs to know.
$rows = @(
    Select-String -LiteralPath $ApiList -Pattern '^\|\s*(GET|POST|DELETE)\s*\|\s*\[(/[^\]\s]+)' `
    | ForEach-Object {
        [pscustomobject]@{
            Method = $_.Matches[0].Groups[1].Value
            Path   = $_.Matches[0].Groups[2].Value
        }
    })

# The count is asserted, not assumed. An extraction that silently matches
# nothing reports a clean sweep, which is the failure this whole file is about.
if ($rows.Count -lt 50) {
    throw "Extracted only $($rows.Count) endpoints from $ApiList - the table format has changed and this probe is measuring nothing."
}

Write-Host "probing $($rows.Count) documented endpoints against $root" -ForegroundColor DarkGray

$missing = @()

foreach ($row in $rows) {
    $path = $row.Path `
        -replace ':sessionId', 'fake-id' `
        -replace ':windowHandle', '0x1234' `
        -replace ':id', 'e1' `
        -replace ':name', 'Name'

    $result = Test-Served -Method $row.Method -Path $path

    if ($result.Served -eq $false) {
        $missing += [pscustomobject]@{ Method = $row.Method; Path = $path }
    }
}

if ($missing.Count -eq 0) {
    Write-Host "clean: all $($rows.Count) documented endpoints answered" -ForegroundColor Green
    return
}

Write-Host ''
Write-Host "$($missing.Count) of $($rows.Count) answered unknown-command:" -ForegroundColor Yellow
$missing | ForEach-Object { Write-Host "  $($_.Method) $($_.Path)" }

Write-Host ''
Write-Host 'Three of these are errors in WinAppDriver''s own table and are expected:' -ForegroundColor DarkGray
Write-Host '  GET /element/e1/element and /elements  - actually POST' -ForegroundColor DarkGray
Write-Host '  GET /element/e1/equals                 - path omits /:other' -ForegroundColor DarkGray
Write-Host 'Confirm any other row by hand before recording it: a doc typo and a real gap look identical from here.' -ForegroundColor DarkGray

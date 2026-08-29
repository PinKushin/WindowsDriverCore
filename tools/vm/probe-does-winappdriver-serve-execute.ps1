<#
    Does WinAppDriver 1.2.1 serve POST /session/:id/execute, and what does it
    accept?

    ASKED BECAUSE THE REPO ASSERTED IT WITHOUT MEASURING. docs/PROTOCOL-AUDIT.md
    recorded "/execute and its windows: vendor commands" as a gap against
    WinAppDriver's API. But /execute is NOT in WinAppDriver's own
    Docs/SupportedAPIs.md (59 endpoints, zero mentions), and none of its samples
    or tests call it. The `windows:` vocabulary is defined by
    appium-windows-driver, the Node driver that WRAPS WinAppDriver - which is a
    different thing to be a complete replacement for.

    So this settles which of the two is true before any code is written:

      served + performs  -> a real gap against the reference
      served + refuses   -> tells us the argument shape it validates
      not served         -> the audit note was wrong, and the goal is
                            appium-windows-driver's surface, not WinAppDriver's

    The probe runs INSIDE the guest, against the reference driver, and writes a
    verdict line per case. It starts its own WinAppDriver and stops it again.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$driver = 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
$root = 'http://127.0.0.1:4723'

if (-not (Test-Path -LiteralPath $driver)) {
    "ABORT: WinAppDriver not found at $driver"
    return
}

# Stop anything already listening, so the version under test is the one started
# here rather than whatever a previous run left behind.
Get-Process WinAppDriver -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$process = Start-Process -FilePath $driver -PassThru -WindowStyle Minimized

# NO -SkipHttpErrorCheck. The guest runs Windows PowerShell 5.1, where that
# parameter does not exist - so every call throws a parameter-binding error, the
# catch reports Code -1, and /status looks dead. The first run of this probe
# aborted for exactly that reason and it read as "WinAppDriver never started".
#
# A non-2xx is an ANSWER here, not a failure: unknown-command is the verdict this
# probe is looking for. So the status code is read back off the exception's
# response, and only a genuine transport failure reports -1.
function Invoke-Wire {
    param([string] $Method, [string] $Path, [string] $Body = '{}')

    # A GET MUST NOT CARRY ONE. .NET refuses outright with "Cannot send a
    # content-body with this verb-type", which surfaces as a transport failure
    # and reads as "the server is down" - which is how the second run of this
    # probe aborted.
    $arguments = @{
        Uri = "$root$Path"
        Method = $Method
        TimeoutSec = 20
        UseBasicParsing = $true
    }

    if ($Method -ne 'GET') {
        $arguments.Body = $Body
        $arguments.ContentType = 'application/json'
    }

    try {
        $response = Invoke-WebRequest @arguments
        return [pscustomobject]@{ Code = [int] $response.StatusCode; Body = $response.Content }
    }
    catch [System.Net.WebException] {
        $web = $_.Exception.Response

        if ($null -eq $web) {
            return [pscustomobject]@{ Code = -1; Body = $_.Exception.Message }
        }

        $reader = New-Object System.IO.StreamReader($web.GetResponseStream())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }

        return [pscustomobject]@{ Code = [int] $web.StatusCode; Body = $text }
    }
    catch {
        return [pscustomobject]@{ Code = -1; Body = $_.Exception.Message }
    }
}

try {
    # Wait for the port by polling the endpoint, not by sleeping on a guess.
    # Forty seconds, matching Invoke-CompatibilitySuite. The guest is a cold VM
    # and the first start is slower than a warm one.
    $ready = $false
    $lastAnswer = 'never called'
    foreach ($attempt in 1..40) {
        $status = Invoke-Wire -Method 'GET' -Path '/status'
        $lastAnswer = "$($status.Code) $($status.Body)"
        if ($status.Code -eq 200) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }

    # The last answer is reported, not just the verdict. "did not answer" and
    # "answered 404" are different problems and the bare verdict hides which.
    if (-not $ready) { "ABORT: WinAppDriver did not answer /status - last: $lastAnswer"; return }

    "version: $((Invoke-Wire -Method 'GET' -Path '/status').Body)"

    $created = Invoke-Wire -Method 'POST' -Path '/session' -Body (
        '{"desiredCapabilities":{"app":"Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"}}')

    if ($created.Code -ne 200) { "ABORT: session create returned $($created.Code): $($created.Body)"; return }

    $session = ($created.Body | ConvertFrom-Json).sessionId
    "session: $session"

    # THE CONTROL FIRST. If an invented route does not report unknown-command,
    # nothing below distinguishes "served" from "unrecognised" and the whole
    # probe is meaningless.
    $control = Invoke-Wire -Method 'POST' -Path "/session/$session/definitely-not-a-route"
    "CONTROL invented-route -> $($control.Code) $($control.Body)"

    $cases = @(
        @{ Name = 'execute, empty';         Path = 'execute';       Body = '{"script":"","args":[]}' },
        @{ Name = 'execute, windows: keys'; Path = 'execute';       Body = '{"script":"windows: keys","args":[{"actions":[{"virtualKeyCode":13}]}]}' },
        @{ Name = 'execute, windows: click';Path = 'execute';       Body = '{"script":"windows: click","args":[{"x":10,"y":10}]}' },
        @{ Name = 'execute, powerShell';    Path = 'execute';       Body = '{"script":"powerShell","args":[{"command":"echo hi"}]}' },
        @{ Name = 'execute_async';          Path = 'execute_async'; Body = '{"script":"","args":[]}' },
        @{ Name = 'alert_text (GET)';       Path = 'alert_text';    Body = $null;  Method = 'GET' },
        @{ Name = 'accept_alert';           Path = 'accept_alert';  Body = '{}' },
        @{ Name = 'dismiss_alert';          Path = 'dismiss_alert'; Body = '{}' },
        @{ Name = 'log/types (GET)';        Path = 'log/types';     Body = $null;  Method = 'GET' },
        @{ Name = 'log';                    Path = 'log';           Body = '{"type":"server"}' },
        @{ Name = 'element submit';         Path = 'element/1/submit'; Body = '{}' },
        @{ Name = 'refresh';                Path = 'refresh';       Body = '{}' },
        @{ Name = 'url (GET)';              Path = 'url';           Body = $null;  Method = 'GET' }
    )

    foreach ($case in $cases) {
        $method = if ($case.ContainsKey('Method')) { $case.Method } else { 'POST' }
        $body = if ($null -eq $case.Body) { '{}' } else { $case.Body }

        $result = Invoke-Wire -Method $method -Path "/session/$session/$($case.Path)" -Body $body

        # Trimmed: some bodies carry a full stack trace and the verdict is in the
        # first line either way.
        $text = ($result.Body -replace '\s+', ' ')
        if ($text.Length -gt 240) { $text = $text.Substring(0, 240) + ' ...' }

        "{0,-22} -> {1} {2}" -f $case.Name, $result.Code, $text
    }

    Invoke-Wire -Method 'DELETE' -Path "/session/$session" | Out-Null
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Get-Process CalculatorApp, Time -ErrorAction SilentlyContinue | Stop-Process -Force
}

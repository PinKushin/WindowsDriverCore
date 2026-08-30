<#
    What window does a SECOND session on an already-running app get?

    MEASURED IN A FULL RUN, 2026-08-30: between the shared Calculator session's
    creation and the tap that failed, Calculator was activated seven more times
    and every activation returned the SAME PROCESS with a DIFFERENT window:

        pid 3912  0x620676  0x540844  0x66080C  0x3A0558
                  0x68046A  0x4505F0  0x5F0768  0x6503F8

    Calculator is single-instance and shows ONE window, so a second activation
    ought to hand back the frame that already exists. The suite keeps one
    long-lived session per application - which is how UI suites are written, and
    the reference copes - so a session pointing at the first handle goes stale
    while later activations move the real window out from under it.

    THE QUESTION THIS ANSWERS, and it is a divergence question rather than a
    theory: given one app already running, what does each driver return for
    sessions two and three?

      same handle every time   -> the reference reuses the frame, and we do not
      a new handle every time  -> both do it, and this is not the divergence

    Both drivers, same app, same script. Four sessions each, all kept OPEN so
    they overlap exactly as the suite's do, and the handles are printed in order.

    IT ALSO ASKS WHETHER THE OLD FRAMES ARE STILL ALIVE, because that separates
    "the handle was recycled" from "there are now four frames and only one of
    them is showing the application".
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ours = 'C:\baseline\host\WindowsDriverCore.exe'
$reference = 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'

if (-not (Test-Path $ours)) { "ABORT: no driver at $ours"; return }
if (-not (Test-Path $reference)) { "ABORT: no WinAppDriver at $reference"; return }

Add-Type -Namespace Probe -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
'@

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

function MeasureDriver([string] $label, [string] $exe) {
    Get-Process WindowsDriverCore, WinAppDriver, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    $server = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
    try {
        $up = $false
        foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $up = $true; break }; Start-Sleep -Seconds 1 }
        if (-not $up) { "ABORT: $label never answered /status"; return }

        ''
        "=== $label ==="

        $sessions = @()
        $handles = @()

        foreach ($n in 1..4) {
            $created = Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}'
            $sid = ($created | ConvertFrom-Json).sessionId
            if (-not $sid) { "  session $n : FAILED - $created"; continue }
            $sessions += $sid

            Start-Sleep -Milliseconds 800
            $h = Value (Wire 'GET' "/session/$sid/window_handle" '{}')
            $handles += $h
            "  session {0} -> window {1}" -f $n, $h
        }

        ''
        $distinct = @($handles | Sort-Object -Unique)
        "  distinct handles: $($distinct.Count) of $($handles.Count)"
        if ($distinct.Count -eq 1) {
            "  VERDICT: the same frame every time - a second session REUSES the window"
        }
        else {
            "  VERDICT: a NEW frame per activation"
            '  are the earlier ones still alive?'
            foreach ($h in $handles) {
                $n = [Convert]::ToInt64(($h -replace '^0x',''), 16)
                $p = [IntPtr]$n
                "    {0}  exists={1}  visible={2}" -f $h, [Probe.Win]::IsWindow($p), [Probe.Win]::IsWindowVisible($p)
            }
        }

        foreach ($sid in $sessions) { Wire 'DELETE' "/session/$sid" '{}' | Out-Null }
    }
    finally {
        if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
        Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

MeasureDriver 'THE REFERENCE (WinAppDriver)' $reference
MeasureDriver 'THIS DRIVER' $ours

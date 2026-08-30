<#
    Does the point MouseDoubleClick clicks actually sit in the window's caption?

    THE OPEN QUESTION LEFT BY THE LAST INVESTIGATION. docs/LIMITATIONS.md
    refuted "a batched double click is too fast" by measurement - the batch DOES
    maximize a window on the host, and two separate /click requests do too, while
    a single click does not. What it could not answer is where the guest's click
    lands: the transcript shows `moveto -> (337,171) of 54x16` against the
    `AppName` element, a small text label, and whether that point is inside the
    ApplicationFrameWindow's draggable caption region on Windows 10 is a question
    only the guest can answer. The host's Calculator is a WinUI app with an
    entirely different title bar.

    WM_NCHITTEST ANSWERS IT DIRECTLY. A window replies with what is at a screen
    point: HTCAPTION means the draggable title bar, which is what double-clicking
    maximizes; HTCLIENT means ordinary content, where a double click does
    nothing of the sort.

    The probe reports the hit-test for:
      - the AppName element's centre, which is what the suite clicks
      - several points across the top of the frame, to find where HTCAPTION
        starts if the first answer is not it

    IT CHANGES NOTHING. No clicks are sent; the window is not maximized. This is
    a question, not an experiment - so it can run without disturbing a suite
    result.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Add-Type -Namespace Probe -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(long p);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out int[] r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
'@

# WM_NCHITTEST, and the replies worth naming. Anything else is reported as its
# number rather than guessed at.
$WM_NCHITTEST = 0x0084
$names = @{
    0 = 'HTNOWHERE'; 1 = 'HTCLIENT'; 2 = 'HTCAPTION'; 3 = 'HTSYSMENU'
    8 = 'HTMINBUTTON'; 9 = 'HTMAXBUTTON'; 20 = 'HTCLOSE'; 18 = 'HTBORDER'
    12 = 'HTTOP'; 13 = 'HTTOPLEFT'; 14 = 'HTTOPRIGHT'; -1 = 'HTTRANSPARENT'
}

function HitTest([IntPtr] $window, [int] $x, [int] $y) {
    # lParam packs y in the high word and x in the low word, both as SIGNED
    # shorts. Packing a negative coordinate as unsigned reports the wrong point
    # entirely, which on a multi-monitor desktop is not hypothetical.
    $l = [IntPtr](([int64]($y -band 0xFFFF) -shl 16) -bor ([int64]($x -band 0xFFFF)))
    $code = [int][Probe.Win]::SendMessage($window, $WM_NCHITTEST, [IntPtr]::Zero, $l)
    if ($names.ContainsKey($code)) { "$($names[$code]) ($code)" } else { "code $code" }
}

$driver = 'C:\baseline\host\WindowsDriverCore.exe'
if (-not (Test-Path $driver)) { "ABORT: no driver at $driver"; return }

Get-Process WindowsDriverCore, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$server = Start-Process -FilePath $driver -PassThru -WindowStyle Minimized

function Wire([string] $method, [string] $path, [string] $body) {
    $args = @{ Uri = "http://127.0.0.1:4723$path"; Method = $method; TimeoutSec = 20; UseBasicParsing = $true }
    if ($method -ne 'GET') { $args.Body = $body; $args.ContentType = 'application/json' }
    try { (Invoke-WebRequest @args).Content }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return $null }
        $reader = New-Object System.IO.StreamReader($r.GetResponseStream())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
}

try {
    $up = $false
    foreach ($i in 1..40) { if (Wire 'GET' '/status' '{}') { $up = $true; break }; Start-Sleep -Seconds 1 }
    if (-not $up) { 'ABORT: driver never answered /status'; return }

    $created = Wire 'POST' '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}'
    $session = ($created | ConvertFrom-Json).sessionId
    if (-not $session) { "ABORT: no session: $created"; return }

    $handleText = (Wire 'GET' "/session/$session/window_handle" '{}' | ConvertFrom-Json).value
    $frame = [IntPtr][Convert]::ToInt64($handleText.Replace('0x',''), 16)

    $class = New-Object System.Text.StringBuilder 256
    [void][Probe.Win]::GetClassName($frame, $class, 256)
    "session window: $handleText  class=$($class.ToString())"

    # The element the suite double-clicks. AppName is what
    # FindCalculatorTitleByAccessibilityId resolves to on this build.
    $found = Wire 'POST' "/session/$session/element" '{"using":"accessibility id","value":"AppName"}'
    $elementId = ($found | ConvertFrom-Json).value.ELEMENT

    if (-not $elementId) { "ABORT: AppName not found: $found"; return }

    $loc = (Wire 'GET' "/session/$session/element/$elementId/location" '{}' | ConvertFrom-Json).value
    $size = (Wire 'GET' "/session/$session/element/$elementId/size" '{}' | ConvertFrom-Json).value

    $cx = [int]$loc.x + [int]([int]$size.width / 2)
    $cy = [int]$loc.y + [int]([int]$size.height / 2)

    ''
    "AppName at ($($loc.x),$($loc.y)) size $($size.width)x$($size.height)"
    "THE POINT THE SUITE CLICKS: ($cx,$cy) -> $(HitTest $frame $cx $cy)"
    ''

    # WHERE THE CAPTION ACTUALLY IS, so a non-HTCAPTION answer above says what to
    # do rather than only that something is wrong.
    'a column down the middle of the frame:'
    $r = 0
    $rect = New-Object 'int[]' 4
    if ([Probe.Win]::GetWindowRect($frame, [ref]$rect)) { $r = 1 }
    $midX = if ($r) { [int](($rect[0] + $rect[2]) / 2) } else { $cx }
    $top = if ($r) { $rect[1] } else { $cy - 40 }

    foreach ($dy in 0, 4, 8, 12, 16, 20, 24, 30, 40, 60) {
        "  ($midX,$($top + $dy))  y+$dy  -> $(HitTest $frame $midX ($top + $dy))"
    }

    Wire 'DELETE' "/session/$session" '{}' | Out-Null
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
}

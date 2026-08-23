# WHERE DOES /touch/down ACTUALLY LAND ON THIS MACHINE?
#
# Watching a probe run, the owner saw a press hit CALCULATOR'S OUTPUT AREA rather
# than its title bar - and that is a coordinate defect, not a gesture one. It was
# never checked here: the equivalent verification was done on the GUEST against
# Alarms & Clock, where it came out exact (window 208 + location 20 = screen 228,
# matching the real rectangle). Windows 11 and Calculator are a different subject
# and the same arithmetic may not hold.
#
# THE COMPARISON, per element:
#
#   /location + /size    what the CLIENT reads back, window-relative
#   computed press       window origin + centre, which is what /touch/down injects
#   BoundingRectangle    the element's REAL screen rectangle, straight from UIA
#
# If "computed press" falls outside the real rectangle, this driver presses the
# wrong pixel and every gesture conclusion above it is void.
#
# It presses NOTHING. Read-only: it launches Calculator, measures, and closes it.

$ErrorActionPreference = 'Continue'

$base = 'http://127.0.0.1:4723'
$CALC = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

$binRoot = Join-Path $PSScriptRoot '..' | Join-Path -ChildPath 'src/WindowsDriverCore.Host/bin'
$exe = Get-ChildItem -Path $binRoot -Filter 'WindowsDriverCore.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'ABORT: no built driver' }
"driver: $exe"

$srv = Start-Process -FilePath $exe -PassThru
for ($i = 0; $i -lt 40; $i++) {
    try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
}

try {
    $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$CALC`"}}").sessionId
    if (-not $session) { throw 'ABORT: no session' }
    Start-Sleep -Seconds 3

    $pos = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
    $size = Invoke-RestMethod -Uri "$base/session/$session/window/current/size" -TimeoutSec 20
    "window: origin $($pos.value.x),$($pos.value.y)  size $($size.value.width)x$($size.value.height)"
    ''
    '  element              /location    /size      computed press   real screen rect                  inside?'
    '  ------------------   ----------   --------   --------------   -------------------------------   -------'

    foreach ($id in 'AppName', 'AppNameTitle', 'TitleBar', 'CalculatorResults', 'num8Button') {
        $el = $null
        try { $el = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT } catch { }
        if (-not $el) { '  {0,-18}   (not found)' -f $id; continue }

        $loc  = Invoke-RestMethod -Uri "$base/session/$session/element/$el/location" -TimeoutSec 20
        $sz   = Invoke-RestMethod -Uri "$base/session/$session/element/$el/size" -TimeoutSec 20
        $rect = Invoke-RestMethod -Uri "$base/session/$session/element/$el/attribute/BoundingRectangle" -TimeoutSec 20

        $px = [int]$pos.value.x + [int]$loc.value.x + [int]([int]$sz.value.width / 2)
        $py = [int]$pos.value.y + [int]$loc.value.y + [int]([int]$sz.value.height / 2)

        # Parse "Left:x Top:y Width:w Height:h" so the verdict is computed, not eyeballed.
        $inside = '?'
        if ($rect.value -match 'Left:(-?\d+).*Top:(-?\d+).*Width:(\d+).*Height:(\d+)') {
            $l = [int]$Matches[1]; $t = [int]$Matches[2]
            $r = $l + [int]$Matches[3]; $b = $t + [int]$Matches[4]
            $inside = if ($px -ge $l -and $px -le $r -and $py -ge $t -and $py -le $b) { 'YES' } else { 'NO  <<<' }
        }

        '  {0,-18}   {1,4},{2,-5}   {3,3}x{4,-4}   {5,6},{6,-7}   {7,-31}   {8}' -f `
            $id, $loc.value.x, $loc.value.y, $sz.value.width, $sz.value.height, $px, $py, $rect.value, $inside
    }

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process CalculatorApp, Calculator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

''
'=== probe complete ==='

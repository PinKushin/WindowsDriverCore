# Is a packaged app's ApplicationFrameWindow reused when the app relaunches?
#
# Every *Error_NoSuchWindow test depends on a CLOSED window's handle answering
# "gone". The orphaned session closes a CALCULATOR window - packaged, hosted in
# an ApplicationFrameWindow owned by ApplicationFrameHost, not by the app.
#
# Handle recycling was already measured and REFUTED for classic windows: 25
# rounds of notepad.exe produced 25 distinct handles and none came back alive.
# But that probe used the wrong subject. A frame window is shell-owned and has a
# different lifetime: ApplicationFrameHost keeps running between launches, so a
# frame may be REUSED for the next activation. If it is, a session that closed
# its window sees that handle report alive again the moment Calculator relaunches
# - which fits every observation: probabilistic (the failures flap run to run)
# and worse when relaunches are frequent (they began failing when Launch_* landed).
#
# PROCESS NAME IS "Calculator" ON WINDOWS 10, not "CalculatorApp". An earlier
# probe cleaned up with the wrong name and left a Calculator running on the guest.

$ErrorActionPreference = 'Continue'

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W {
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@

function ClassOf($h) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][W]::GetClassName($h, $sb, 256)
    $sb.ToString()
}
function TitleOf($h) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][W]::GetWindowText($h, $sb, 256)
    $sb.ToString()
}
function OwnerPid($h) {
    $p = 0
    [void][W]::GetWindowThreadProcessId($h, [ref]$p)
    $p
}

function LaunchCalculatorAndGetFrame {
    Start-Process 'explorer.exe' 'shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $frame = Get-Process ApplicationFrameHost -ErrorAction SilentlyContinue |
            ForEach-Object { $_.MainWindowHandle } | Where-Object { $_ -ne 0 } | Select-Object -First 1
        if ($frame) {
            $title = TitleOf $frame
            if ($title -match 'Calculator') { return $frame }
        }
    }
    return [IntPtr]::Zero
}

Get-Process Calculator -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$frames = @()

for ($round = 1; $round -le 6; $round++) {
    $frame = LaunchCalculatorAndGetFrame
    if ($frame -eq [IntPtr]::Zero) { "round $round : no Calculator frame appeared"; continue }

    $value = [int64]$frame
    "round {0}: frame 0x{1:X}  class={2}  title='{3}'  ownerPid={4}" -f `
        $round, $value, (ClassOf $frame), (TitleOf $frame), (OwnerPid $frame)

    $seenBefore = $frames | Where-Object { $_.Handle -eq $value }
    if ($seenBefore) {
        "   *** SAME FRAME HANDLE as round $($seenBefore[0].Round) - the frame was REUSED"
    }

    $frames += [pscustomobject]@{ Round = $round; Handle = $value }

    # Close the app the way the driver does: the process, not the frame.
    Get-Process Calculator -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(4000)) { $_.Kill() }
    }
    Start-Sleep -Milliseconds 800

    $alive = [W]::IsWindow([IntPtr]$value)
    "   after close: IsWindow={0}  visible={1}  title='{2}'" -f `
        $alive, ([W]::IsWindowVisible([IntPtr]$value)), (TitleOf ([IntPtr]$value))
}

""
"=== RESULT ==="
$distinct = ($frames | Select-Object -ExpandProperty Handle -Unique).Count
"frames observed: $($frames.Count), distinct handle values: $distinct"

if ($distinct -lt $frames.Count) {
    "FRAME HANDLES WERE REUSED ACROSS RELAUNCHES."
    "  => a session that closed its window can see that handle report ALIVE again"
    "     once the app relaunches, which is the *Error_NoSuchWindow regression."
    "  => existence is not identity: the session must also check the window still"
    "     belongs to it, not merely that something answers to the handle."
} else {
    "every relaunch produced a DISTINCT frame handle - frame reuse does NOT"
    "explain the regression either, and the cause is still open."
}

""
"=== do any earlier frame handles report alive now? ==="
$alive = @()
foreach ($f in $frames) {
    if ([W]::IsWindow([IntPtr]$f.Handle)) {
        $alive += "  0x{0:X} from round {1} reports ALIVE, class={2} title='{3}' pid={4}" -f `
            $f.Handle, $f.Round, (ClassOf ([IntPtr]$f.Handle)), (TitleOf ([IntPtr]$f.Handle)), (OwnerPid ([IntPtr]$f.Handle))
    }
}
if ($alive.Count -gt 0) { "SURVIVING FRAMES ($($alive.Count)):"; $alive | ForEach-Object { $_ } }
else { "  none survive" }

Get-Process Calculator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
'=== probe complete ==='

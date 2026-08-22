# Why is a touch UP refused after a long interpolated move?
#
# FOUR MEASUREMENTS THROUGH THE DRIVER, AND THEY CONTRADICT EACH OTHER:
#
#   a085cd6  /touch/move ~1 ms, one frame                /touch/up 200
#   7f02766  /touch/move 314 ms, paced, trailing gap     /touch/up 500 "refused (Up)"
#   b2dd934  /touch/move 2.2 ms, no pacing               /touch/up 200
#   43d510b  /touch/move 414 ms, paced, NO trailing gap  /touch/up 500 "refused (Up)"
#
# The duration breaks the lift and the trailing gap does not. But every UPDATE
# frame during the move SUCCEEDS - a failed frame returns a different message
# and a 500 on the move itself. So the contact is demonstrably alive across ten
# frames and dead by the lift, and the gaps DURING the loop are the same size as
# the one before it. That is the contradiction.
#
# IT USES THE DRIVER'S OWN INJECTION CODE, ON PURPose.
#
# A first version of this probe re-declared POINTER_TOUCH_INFO in PowerShell and
# every single case failed at DOWN with ERROR_INVALID_PARAMETER - including the
# control, which is the configuration the driver measures as WORKING. The struct
# layout was wrong, so the probe was measuring its own P/Invoke and nothing else.
# Loading WindowsDriverCore.Platform.dll and calling SyntheticPointer removes
# that entire class of error: this is the exact code the compatibility suite
# runs, with only the TIMING varied.
#
# WHAT IT VARIES, one axis at a time, against one window:
#
#   frames    how many UPDATE frames the move is broken into
#   gap       milliseconds between frames
#   lift gap  milliseconds between the last UPDATE and the UP
#
# THE CONTROL IS THE FIRST ROW - one frame, no gaps, which the driver measured
# as working at b2dd934. If that fails here the harness is wrong and no other
# row means anything, which is exactly what the first version got right by
# failing loudly instead of reporting eight plausible lines.

$ErrorActionPreference = 'Continue'

# GUEST ONLY, AND THIS GUARD IS NOT OPTIONAL.
#
# This probe injects real touch contacts at absolute SCREEN coordinates. Run on
# the host it would press and drag on whatever is under those pixels - and this
# project has already typed into a user's chat window once by synthesising input
# outside the guest.
#
# C:\baseline exists only in the measurement guest, which has its own desktop and
# no human on it.
if (-not (Test-Path 'C:\baseline')) {
    throw 'REFUSED: this probe injects real touch input and must only run in the Win10 guest (C:\baseline not found).'
}

Add-Type -Namespace Probe -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
'@

$platform = 'C:\baseline\host\WindowsDriverCore.Platform.dll'
$contracts = 'C:\baseline\host\WindowsDriverCore.Contracts.dll'
foreach ($dll in @($contracts, $platform)) {
    if (-not (Test-Path $dll)) { throw "ABORT: $dll not found - nothing to measure." }
    Add-Type -Path $dll
}

$injector = New-Object WindowsDriverCore.Platform.Windows.SyntheticPointer
$Touch = [WindowsDriverCore.Platform.Windows.SyntheticPointerKind]::Touch

if (-not $injector.CanInject($Touch)) { throw 'ABORT: this system cannot inject touch.' }

$Down   = [WindowsDriverCore.Platform.Windows.SyntheticContactPhase]::Down
$Update = [WindowsDriverCore.Platform.Windows.SyntheticContactPhase]::Update
$Up     = [WindowsDriverCore.Platform.Windows.SyntheticContactPhase]::Up

function Frame([int] $x, [int] $y, $phase) {
    $c = New-Object WindowsDriverCore.Platform.Windows.SyntheticContact `
        -ArgumentList $Touch, $x, $y, $phase, 0.5, 0, 0
    $list = New-Object 'System.Collections.Generic.List[WindowsDriverCore.Platform.Windows.SyntheticContact]'
    $list.Add($c)
    return $list
}

function InjectOne([int] $x, [int] $y, $phase) {
    $ok = $injector.Inject((Frame $x $y $phase))
    if ($ok) { return 0 }
    return [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
}

function ErrorName([int] $code) {
    switch ($code) {
        0     { 'ok' }
        5     { 'ERROR_ACCESS_DENIED' }
        87    { 'ERROR_INVALID_PARAMETER' }
        1121  { 'ERROR_TIMEOUT' }
        default { "error $code" }
    }
}

function Gesture([int] $frames, [int] $gapMs, [int] $liftGapMs, [int] $x, [int] $y) {
    $d = InjectOne $x $y $Down
    if ($d -ne 0) { return "DOWN failed: $(ErrorName $d)" }

    for ($i = 1; $i -le $frames; $i++) {
        if ($gapMs -gt 0) { Start-Sleep -Milliseconds $gapMs }
        $stepX = $x + [int](100 * $i / $frames)
        $stepY = $y + [int](100 * $i / $frames)
        $u = InjectOne $stepX $stepY $Update
        if ($u -ne 0) { return "UPDATE $i of $frames failed: $(ErrorName $u)" }
    }

    if ($liftGapMs -gt 0) { Start-Sleep -Milliseconds $liftGapMs }

    $l = InjectOne ($x + 100) ($y + 100) $Up
    if ($l -ne 0) { return "UP failed: $(ErrorName $l)" }
    return 'whole gesture ok'
}

Get-Process Time, WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# CHARMAP, NOT A PACKAGED APP. The question is about InjectTouchInput's own
# behaviour, not about any application. A packaged app drags in frame/CoreWindow
# hosting and, measured twice on this guest, will not activate from the agent's
# session at all - both this probe and probe-drain-on-packaged-app.ps1 reported
# "no window" trying exactly that.
$app = Start-Process charmap -PassThru
Start-Sleep -Seconds 3

$target = $app.MainWindowHandle
if ($target -eq [IntPtr]::Zero) { throw 'ABORT: charmap has no window - nothing to measure.' }

$r = New-Object Probe.Win+RECT
[void][Probe.Win]::GetWindowRect($target, [ref] $r)
[void][Probe.Win]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 500

# Well inside the window, away from the caption buttons and the edges.
$x = $r.Left + 60
$y = $r.Top + 60
"window {0},{1}-{2},{3}   touching {4},{5}" -f $r.Left, $r.Top, $r.Right, $r.Bottom, $x, $y

''
'  frames  gap  liftGap   result'
'  ------  ---  -------   ------'

foreach ($case in @(
    @{ frames = 1;  gap = 0;   lift = 0   }   # CONTROL - the driver measures this as working
    @{ frames = 10; gap = 0;   lift = 0   }   # many frames, no time   - b2dd934 shape
    @{ frames = 10; gap = 30;  lift = 0   }   # 300 ms, no trailing    - 43d510b shape
    @{ frames = 10; gap = 30;  lift = 30  }   # 300 ms with trailing   - 7f02766 shape
    @{ frames = 10; gap = 46;  lift = 0   }   # 414 ms, the slow-guest timing
    @{ frames = 1;  gap = 300; lift = 0   }   # ONE long gap, then lift
    @{ frames = 1;  gap = 0;   lift = 300 }   # short move, LONG lift gap
    @{ frames = 30; gap = 10;  lift = 0   }   # same 300 ms, refreshed 3x as often
)) {
    $result = Gesture $case.frames $case.gap $case.lift $x $y
    '  {0,6}  {1,3}  {2,7}   {3}' -f $case.frames, $case.gap, $case.lift, $result

    # A failed gesture may leave a contact down. Lift it before the next case so
    # one failure cannot cascade into the rest of the table.
    [void](InjectOne ($x + 100) ($y + 100) $Up)
    Start-Sleep -Milliseconds 800
}

Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
''
'=== probe complete ==='

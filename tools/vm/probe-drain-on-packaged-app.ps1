# Does the typed-input drain actually WAIT against a packaged application?
#
# MEASURED so far:
#   - The /text read that fails is 0.9 ms after the Delete that should have
#     changed it. The drain is armed (POST /value sets InputPending, GET /text
#     calls DrainTypedInput) and costs approximately nothing.
#   - focused=48 unfocused=0 across the whole run, so SetFocus is not it.
#   - Six repeated drains against the repository's Win32 app read back 52/52,
#     so "WaitForInputIdle waits only once per process" is REFUTED.
#
# The difference between the subject that works and the one that fails is the
# SUBJECT: a single-process Win32 app whose handle is its own window, versus a
# packaged app behind an ApplicationFrameWindow owned by ApplicationFrameHost.
#
# WaitForInputProcessed returns false on three paths -- window gone, pid 0, or
# OpenProcess denied -- and the caller DISCARDS the result. So a drain that never
# ran and a drain that waited are the same observation.
#
# This asks the guest directly, against the real Alarms & Clock:
#
#   owner / hosted pid   are they different (is the broker in the path at all)
#   OpenProcess          with PROCESS_QUERY_INFORMATION | SYNCHRONIZE, the rights
#                        the driver asks for -- and separately with
#                        PROCESS_QUERY_LIMITED_INFORMATION, the weaker right that
#                        is expected to succeed against an AppContainer target
#   WaitForInputIdle     elapsed ms, immediately after typing 52 characters
#
# THE CONTROL IS THE SAME MEASUREMENT AGAINST NOTEPAD-LIKE OWN WINDOW. Without
# it, "OpenProcess failed" cannot be told from "this probe asks for the rights
# wrongly" -- the call would fail for both subjects and prove nothing.
#
# It does NOT go through the driver. The driver's own transcript already showed
# the symptom; what is unknown is which Win32 call underneath is refusing, and
# that answer must not be filtered through the layer being questioned.

$ErrorActionPreference = 'Continue'

# GUEST ONLY, AND THIS GUARD IS NOT OPTIONAL.
#
# This probe calls SendKeys::SendWait, which delivers to whatever holds the
# FOREGROUND rather than to a window handle. Run on the host it types 52
# characters into whatever the user is looking at -- which has already happened
# once in this project, into a chat window.
#
# C:\baseline exists only in the measurement guest, which has its own desktop and
# no human on it.
if (-not (Test-Path 'C:\baseline')) {
    throw 'REFUSED: this probe synthesises keyboard input at the foreground and must only run in the Win10 guest (C:\baseline not found).'
}

Add-Type -Namespace Probe -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
[DllImport("user32.dll")] public static extern uint WaitForInputIdle(IntPtr proc, uint ms);
[DllImport("user32.dll")] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string name);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder buf, int max);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
'@

function WindowClass([IntPtr] $h) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][Probe.Native]::GetClassNameW($h, $sb, 256)
    return $sb.ToString()
}

$QUERY_INFORMATION         = 0x0400
$QUERY_LIMITED_INFORMATION = 0x1000
$SYNCHRONIZE               = 0x00100000

function TryOpen([uint32] $pid_, [uint32] $access, [string] $label) {
    $h = [Probe.Native]::OpenProcess($access, $false, $pid_)
    if ($h -eq [IntPtr]::Zero) {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        # Write-Host, NOT bare output. A bare string joins the return value and
        # $handle becomes Object[] - which the first version did, and the failed
        # conversion still printed a plausible "rc  17.6 ms" line.
        Write-Host ("    {0,-42} DENIED (GetLastError {1})" -f $label, $err)
        return [IntPtr]::Zero
    }
    Write-Host ("    {0,-42} OK" -f $label)
    return $h
}

function ProbeWindow([string] $label, [IntPtr] $hwnd) {
    ""
    "=== $label"
    if ($hwnd -eq [IntPtr]::Zero) { "    no window"; return }

    $owner = 0
    [void][Probe.Native]::GetWindowThreadProcessId($hwnd, [ref] $owner)

    # The hosted process, the way the driver finds it: the CoreWindow child of
    # an ApplicationFrameWindow belongs to the app, the frame to the broker.
    $core = [Probe.Native]::FindWindowExW($hwnd, [IntPtr]::Zero, 'Windows.UI.Core.CoreWindow', $null)
    $hosted = $owner
    if ($core -ne [IntPtr]::Zero) {
        $h2 = 0
        [void][Probe.Native]::GetWindowThreadProcessId($core, [ref] $h2)
        $hosted = $h2
    }

    Write-Host ("    hwnd 0x{0:X}  owner pid {1}  hosted pid {2}  broker in path: {3}" -f `
        [int64]$hwnd, $owner, $hosted, $(if ($owner -ne $hosted) { 'YES' } else { 'no' }))

    $strong = TryOpen $hosted ($QUERY_INFORMATION -bor $SYNCHRONIZE) 'PROCESS_QUERY_INFORMATION|SYNCHRONIZE'
    $weak   = TryOpen $hosted ($QUERY_LIMITED_INFORMATION -bor $SYNCHRONIZE) 'PROCESS_QUERY_LIMITED_INFORMATION|SYNC'

    foreach ($pair in @(@('strong', $strong), @('weak', $weak))) {
        $name = $pair[0]; $handle = $pair[1]
        if ($handle -eq [IntPtr]::Zero) { continue }

        [void][Probe.Native]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 400

        # 52 characters, because the original measurement read back 1 of 52 with
        # no wait -- a condition where waiting and not waiting differ visibly.
        [System.Windows.Forms.SendKeys]::SendWait('abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ')

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $rc = [Probe.Native]::WaitForInputIdle($handle, 5000)
        $sw.Stop()

        $meaning = switch ($rc) {
            0          { 'IDLE (the wait worked)' }
            258        { 'WAIT_TIMEOUT' }
            4294967295 { 'WAIT_FAILED - not a GUI process, or no message queue' }
            default    { "unknown $rc" }
        }
        Write-Host ("    WaitForInputIdle via {0,-6} -> {1,-42} {2:N1} ms" -f $name, $meaning, $sw.Elapsed.TotalMilliseconds)
        [void][Probe.Native]::CloseHandle($handle)
    }
}

Add-Type -AssemblyName System.Windows.Forms

Get-Process Time, WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# THE SUBJECT: the packaged application the suite drives.
# THE AUMID, not the protocol handler. Start-Process 'ms-clock:' returned
# without activating anything in the agent's session and the probe reported
# "no window" twice - a skip that reads as a result. explorer shell:appsFolder is
# the activation that works for a packaged application from a script.
Start-Process 'explorer.exe' -ArgumentList 'shell:appsFolder\Microsoft.WindowsAlarms_8wekyb3d8bbwe!App'
Start-Sleep -Seconds 10
# NOT MainWindowHandle, AND NOT Get-Process. A packaged app does not own its
# frame, so its MainWindowHandle is 0; the frame belongs to ApplicationFrameHost,
# whose MainWindowHandle is only ONE of the frames it hosts. Both earlier versions
# of this probe read nothing and reported "no window" while still printing a
# control -- a skip that reads as a result.
#
# EnumWindows over every visible top level, matched by CLASS and by the process
# behind the CoreWindow child, which is how the driver identifies it too.
$script:alarms = [IntPtr]::Zero
$cb = [Probe.Native+EnumProc] {
    param([IntPtr] $h, [IntPtr] $unused)
    if ([Probe.Native]::IsWindowVisible($h) -and (WindowClass $h) -eq 'ApplicationFrameWindow') {
        $core = [Probe.Native]::FindWindowExW($h, [IntPtr]::Zero, 'Windows.UI.Core.CoreWindow', $null)
        if ($core -ne [IntPtr]::Zero) {
            $cp = 0
            [void][Probe.Native]::GetWindowThreadProcessId($core, [ref] $cp)
            $n = (Get-Process -Id $cp -ErrorAction SilentlyContinue).ProcessName
            Write-Host ("    candidate frame 0x{0:X} hosting pid {1} ({2})" -f [int64]$h, $cp, $n)
            if ($n -eq 'Time') { $script:alarms = $h }
        }
    }
    return $true
}
[void][Probe.Native]::EnumWindows($cb, [IntPtr]::Zero)
$alarms = $script:alarms

ProbeWindow 'Alarms & Clock (packaged, behind ApplicationFrameWindow)' $alarms

# THE CONTROL, AND IT MUST BE A GUI PROCESS. WaitForInputIdle returns WAIT_FAILED
# (rc 4294967295) for a CONSOLE process, so the first version of this control
# measured powershell.exe and reported failure for a reason that had nothing to
# do with packaging. charmap is a plain Win32 GUI application and owns its window.
$ctl = Start-Process charmap -PassThru
Start-Sleep -Seconds 3
ProbeWindow 'charmap (Win32 GUI, owns its own window)' $ctl.MainWindowHandle
Stop-Process -Id $ctl.Id -Force -ErrorAction SilentlyContinue

Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
''
'=== probe complete ==='

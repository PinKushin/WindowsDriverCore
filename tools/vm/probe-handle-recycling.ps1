# Does Windows reuse an HWND value, and can a dead handle report alive again?
#
# Every *Error_NoSuchWindow test depends on a CLOSED window's handle answering
# "gone". This driver asks IsWindow(handle), which is a question about the
# VALUE, not about identity. If Windows reuses handle values then a later window
# can inherit a dead session's handle and the session sees it as alive - which
# would explain both the flapping (ActionsError_NoSuchWindow has flipped every
# run) and why it worsened when Launch_* started creating more windows.
#
# REAL APPLICATIONS ONLY. A synthesised top-level window with no message pump
# blocks every broadcast SendMessage and wedges the desktop; that was measured
# here before and must not be repeated.
#
# Classic notepad.exe is used because this guest is Windows 10, where it is a
# plain Win32 application that opens and closes without the restore behaviour a
# packaged app has.

$ErrorActionPreference = 'Continue'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@

$rounds = 25
$seen = @{}          # handle value -> the round that first produced it
$reuse = @()
$deadButAlive = @()
$closed = @()        # handles we have closed, with the pid that owned them

"launching and closing classic Notepad $rounds times..."

for ($i = 1; $i -le $rounds; $i++) {
    $p = Start-Process 'C:\Windows\System32\notepad.exe' -PassThru
    $handle = [IntPtr]::Zero
    for ($w = 0; $w -lt 50; $w++) {
        Start-Sleep -Milliseconds 100
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { $handle = $p.MainWindowHandle; break }
    }
    if ($handle -eq [IntPtr]::Zero) { "  round $i : no window appeared"; continue }

    $value = [int64]$handle
    if ($seen.ContainsKey($value)) {
        $reuse += "  REUSED 0x{0:X} - first seen in round {1}, again in round {2}" -f $value, $seen[$value], $i
    }
    $seen[$value] = $i

    [void]([W]::GetWindowThreadProcessId($handle, [ref]([uint32]0)))
    $pid0 = 0
    [W]::GetWindowThreadProcessId($handle, [ref]$pid0) | Out-Null

    # Close it the way the driver does, then confirm it really is gone.
    $p.CloseMainWindow() | Out-Null
    $p.WaitForExit(5000) | Out-Null
    if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(3000) | Out-Null }
    Start-Sleep -Milliseconds 200

    $aliveNow = [W]::IsWindow($handle)
    if ($aliveNow) {
        $deadButAlive += "  round {0}: 0x{1:X} still reports IsWindow=TRUE right after close" -f $i, $value
    }
    $closed += [pscustomobject]@{ Round = $i; Handle = $value; Pid = $pid0 }
}

""
"=== RESULT ==="
"distinct handle values over $rounds rounds : $($seen.Keys.Count)"

if ($reuse.Count -gt 0) {
    "HANDLE VALUES WERE REUSED ($($reuse.Count)):"
    $reuse | ForEach-Object { $_ }
} else {
    "no handle value was reused within this run"
}

if ($deadButAlive.Count -gt 0) {
    "A CLOSED HANDLE REPORTED ALIVE ($($deadButAlive.Count)):"
    $deadButAlive | ForEach-Object { $_ }
} else {
    "every closed handle reported IsWindow=false immediately after close"
}

# The decisive one: do any of the EARLIER closed handles report alive now, after
# all the later windows have been created? That is the exact condition an
# orphaned session sits in - it closed its window long before the later tests run.
""
"=== do earlier closed handles report alive AFTER later window churn? ==="
$resurrected = @()
foreach ($entry in $closed) {
    if ([W]::IsWindow([IntPtr]$entry.Handle)) {
        $nowPid = 0
        [W]::GetWindowThreadProcessId([IntPtr]$entry.Handle, [ref]$nowPid) | Out-Null
        $resurrected += "  0x{0:X} closed in round {1} (pid {2}) now reports ALIVE, owned by pid {3}" -f `
            $entry.Handle, $entry.Round, $entry.Pid, $nowPid
    }
}
if ($resurrected.Count -gt 0) {
    "RESURRECTED HANDLES ($($resurrected.Count)) - this is the defect:"
    $resurrected | ForEach-Object { $_ }
    "  => IsWindow is a question about the VALUE, not identity. A session must"
    "     also verify the owning process to know its own window is gone."
} else {
    "  none - no closed handle came back alive, so recycling does NOT explain the regression"
}

Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
'=== probe complete ==='

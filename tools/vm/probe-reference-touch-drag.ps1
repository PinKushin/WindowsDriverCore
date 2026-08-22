# What does the REFERENCE do for a multi-request touch drag?
#
# TouchDownMoveUp_DragAndDrop passes against WinAppDriver on this guest and fails
# against this driver. Five candidates have been refuted by measurement - a
# timeout, the frame count, the coordinates, the trailing pacing gap, and thread
# affinity - and the only variable left that moves the result is how long the
# move takes. Three attempts at tuning that produced one more row each and no
# understanding.
#
# SO ASK THE REFERENCE, which is cheaper than decompiling it and answers a
# different, better question. Decompilation would show HOW WinAppDriver injects;
# this shows WHAT IT DOES, observably, which is the thing that has to be matched.
# The API is the contract - the implementation is not.
#
# THE MEASUREMENT THAT DECIDES IT:
#
#   /touch/move elapsed   if the reference's move is ~1 ms and the window STILL
#                         moves, then duration was never the mechanism and four
#                         runs were spent on the wrong variable.
#   window moved          the only thing the suite actually asserts.
#   /touch/up status      ours is refused with ERROR_INVALID_PARAMETER after a
#                         long move; does the reference ever see that at all.
#
# Both drivers, same window, same gesture, in one run - so the comparison is not
# across machines, sessions or app versions.
#
# THE ELEMENT KEY IS THE JWP SPELLING. This talks to WinAppDriver, which is a
# Selenium 2 server: an actions origin keyed the W3C way was measured being
# rejected by it with 400. Only the classic /touch routes are used here, which
# take plain x/y, so the question does not arise for the gesture itself.

$ErrorActionPreference = 'Continue'

# GUEST ONLY, AND THIS GUARD IS NOT OPTIONAL.
#
# It drives a real driver that injects real touch at screen coordinates and drags
# a real window. On the host it would drag whatever is under those pixels.
if (-not (Test-Path 'C:\baseline')) {
    throw 'REFUSED: this probe injects real touch input and must only run in the Win10 guest (C:\baseline not found).'
}

$base = 'http://127.0.0.1:4723'
$ALARMS = 'Microsoft.WindowsAlarms_8wekyb3d8bbwe!App'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

function MeasureDriver($driverName, $exePath) {
    ''
    '=========================================================='
    "  $driverName"
    '=========================================================='

    Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    $srv = Start-Process -FilePath $exePath -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    try {
        $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$ALARMS`"}}").sessionId
        if (-not $session) { '  no session'; return }
        Start-Sleep -Seconds 2

        $before = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
        "  window before : x=$($before.value.x) y=$($before.value.y)"

        # The title bar, exactly as the suite finds it.
        $title = $null
        foreach ($id in 'AppName','AppNameTitle','TitleBar') {
            try {
                $title = (PostRaw "/session/$session/element" "{`"using`":`"accessibility id`",`"value`":`"$id`"}").value.ELEMENT
                if ($title) { "  title bar via '$id'"; break }
            } catch { }
        }
        if (-not $title) { '  ABORT: no title bar element, nothing to drag'; return }

        $loc = Invoke-RestMethod -Uri "$base/session/$session/element/$title/location" -TimeoutSec 20
        $size = Invoke-RestMethod -Uri "$base/session/$session/element/$title/size" -TimeoutSec 20
        $x = [int]$loc.value.x + [int]($size.value.width / 2)
        $y = [int]$loc.value.y + [int]($size.value.height / 2)
        "  touching      : $x,$y (window-relative, mid title bar)"

        # THE GESTURE THE SUITE PERFORMS: three separate requests.
        $timings = @{}
        foreach ($phase in 'down','move','up') {
            $tx = if ($phase -eq 'down') { $x } else { $x + 100 }
            $ty = if ($phase -eq 'down') { $y } else { $y + 100 }

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $status = 'ok'
            try { PostRaw "/session/$session/touch/$phase" "{`"x`":$tx,`"y`":$ty}" | Out-Null }
            catch {
                # THE BODY, READ OFF THE RESPONSE STREAM. The fault message is
                # where the driver reports WHY - the Win32 error and the thread
                # each phase ran on - and "500 Internal Server Error" alone hides
                # exactly that.
                #
                # $_.ErrorDetails.Message is empty here: this is Windows
                # PowerShell 5.1, where Invoke-RestMethod does not populate it for
                # a 500 with a JSON body. That cost a probe run. The stream is
                # the reliable source.
                $status = "FAILED: $($_.Exception.Message)"
                $resp = $_.Exception.Response
                if ($resp) {
                    try {
                        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                        $body = $reader.ReadToEnd()
                        $reader.Close()
                        if ($body) { $status = "FAILED: $body" }
                    } catch { }
                }
            }
            $sw.Stop()

            $timings[$phase] = $sw.Elapsed.TotalMilliseconds
            "  /touch/{0,-5} {1,8:N1} ms   {2}" -f $phase, $sw.Elapsed.TotalMilliseconds, $status
        }

        Start-Sleep -Seconds 1
        $after = Invoke-RestMethod -Uri "$base/session/$session/window/current/position" -TimeoutSec 20
        "  window after  : x=$($after.value.x) y=$($after.value.y)"
        "  MOVED         : {0}" -f $(if ($before.value.x -ne $after.value.x -or $before.value.y -ne $after.value.y) { 'YES' } else { 'NO' })

        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
    }
    finally {
        Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
        Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}


# HOW LONG CAN A CONTACT BE HELD BEFORE THE LIFT IS REFUSED?
#
# Isolates HOLDING from MOVING. No move at all - down, wait, up - so a refusal
# cannot be blamed on interpolation, frame count, coordinates or pacing, every
# one of which has already been refuted separately.
#
# The reference holds a contact for roughly 370 ms across its own gesture and
# lifts cleanly; ours was refused after a 156 ms move. If ours also fails a bare
# HOLD of that length then the contact is simply not being kept alive, and the
# reference must be doing something to keep it so. If ours SURVIVES a long bare
# hold, then holding is fine and the move itself is what kills it - a different
# answer entirely.
function MeasureHold($driverName, $exePath) {
    ''
    '=========================================================='
    "  HOLD TEST - $driverName"
    '=========================================================='

    Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    $srv = Start-Process -FilePath $exePath -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; break } catch { Start-Sleep -Seconds 1 }
    }

    try {
        $session = (PostRaw '/session' "{`"desiredCapabilities`":{`"app`":`"$ALARMS`"}}").sessionId
        if (-not $session) { '  no session'; return }
        Start-Sleep -Seconds 2

        '  hold(ms)   up result'
        '  --------   ---------'
        foreach ($hold in 0, 50, 100, 200, 400, 800) {
            # A harmless point inside the client area, not the title bar - this
            # is about the contact's lifetime, and dragging the window would move
            # the target between iterations.
            $px = 60
            $py = 120

            $status = 'ok'
            try { PostRaw "/session/$session/touch/down" "{`"x`":$px,`"y`":$py}" | Out-Null }
            catch { $status = 'DOWN FAILED' }

            if ($hold -gt 0) { Start-Sleep -Milliseconds $hold }

            if ($status -eq 'ok') {
                try { PostRaw "/session/$session/touch/up" "{`"x`":$px,`"y`":$py}" | Out-Null }
                catch { $status = "UP REFUSED" }
            }

            '  {0,8}   {1}' -f $hold, $status
            Start-Sleep -Milliseconds 600
        }

        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 20 | Out-Null } catch { }
    }
    finally {
        Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
        Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

MeasureDriver 'WinAppDriver 1.2.1 (the reference)' 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
MeasureDriver 'WindowsDriverCore (ours)'           'C:\baseline\host\WindowsDriverCore.exe'

MeasureHold 'WindowsDriverCore (ours)'           'C:\baseline\host\WindowsDriverCore.exe'
MeasureHold 'WinAppDriver 1.2.1 (the reference)' 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'

''
'=== probe complete ==='
''
'READ IT LIKE THIS:'
'  reference move ~1 ms AND moved YES -> duration is NOT the mechanism, and the'
'                                        difference is in what is injected, not when.'
'  reference move slow  AND moved YES -> duration matters and the reference simply'
'                                        survives it, so the question becomes why'
'                                        our contact does not.'

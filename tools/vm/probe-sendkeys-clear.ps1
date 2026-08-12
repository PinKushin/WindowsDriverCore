# Does a modifier survive a SendKeys request boundary, and does Ctrl+A / Delete clear?
#
# ElementSendKeys.TestInit clears the box with TWO separate requests:
#
#     alarmNameTextBox.SendKeys(Keys.Control + "a");   # Ctrl pressed, never released in this string
#     alarmNameTextBox.SendKeys(Keys.Delete);          # does this arrive as Delete, or Ctrl+Delete?
#     Assert.AreEqual(string.Empty, alarmNameTextBox.Text);
#
# THE FIRST VERSION OF THIS PROBE WAS INVALID and its result must not be reused.
# It built the body with ConvertTo-Json and posted it as a string, which
# transcoded U+E009 to a literal '?' before it left PowerShell: both drivers
# dutifully typed "?a" and appeared to agree. Agreement produced by the
# instrument is not evidence about the subject.
#
# The body is now hand-built as ASCII-only JSON with \uE009 escapes, which no
# encoding step can mangle, and posted as explicit UTF-8 bytes.

$ErrorActionPreference = 'Continue'
$base = 'http://127.0.0.1:4723'

function PostRaw($path, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 30
}

function Probe($driverName, $exePath) {
    "=========================================================="
    "  $driverName"
    "=========================================================="

    Get-Process WinAppDriver, WindowsDriverCore -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2

    $srv = Start-Process -FilePath $exePath -PassThru
    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; $up = $true; break }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $up) { "ABORT: $driverName never answered"; return }

    try {
        $session = (PostRaw '/session' '{"desiredCapabilities":{"app":"Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"}}').sessionId
        Start-Sleep -Seconds 3

        function Find($using, $value) {
            try { (PostRaw "/session/$session/element" "{`"using`":`"$using`",`"value`":`"$value`"}").value.ELEMENT }
            catch { $null }
        }
        function TextOf($el) {
            try { (Invoke-RestMethod -Uri "$base/session/$session/element/$el/text" -TimeoutSec 15).value }
            catch { "<read failed>" }
        }
        # keysJson is a JSON array body fragment, already escaped.
        function Send($el, $keysJson) {
            try { PostRaw "/session/$session/element/$el/value" "{`"value`":[$keysJson]}" | Out-Null }
            catch { "   SEND FAILED: $($_.Exception.Message)" }
        }

        $box = Find 'accessibility id' 'AlarmNameTextBox'
        if (-not $box) {
            $add = Find 'accessibility id' 'AddAlarmButton'
            if ($add) { PostRaw "/session/$session/element/$add/click" '{}' | Out-Null; Start-Sleep -Seconds 2 }
            $box = Find 'accessibility id' 'AlarmNameTextBox'
        }
        if (-not $box) { "ABORT: no AlarmNameTextBox"; return }

        Send $box '"hello"'
        Start-Sleep -Milliseconds 700
        "1. typed 'hello'                -> '$(TextOf $box)'"

        # THE SEQUENCE UNDER TEST, two separate requests as TestInit issues them.
        Send $box '"\uE009a"'
        Start-Sleep -Milliseconds 700
        "2. SendKeys(Ctrl+'a')           -> '$(TextOf $box)'   (expect 'hello', now selected)"

        Send $box '"\uE017"'
        Start-Sleep -Milliseconds 700
        $afterDelete = TextOf $box
        "3. SendKeys(Delete)             -> '$afterDelete'   (TestInit REQUIRES empty)"

        # THE PERSISTENCE MEASUREMENT. If Ctrl is still physically down across
        # the request boundary, a plain 'x' arrives as Ctrl+X (cut) rather than
        # as the letter, so the reading separates the two without inference.
        Send $box '"x"'
        Start-Sleep -Milliseconds 700
        $afterX = TextOf $box
        "4. SendKeys('x')                -> '$afterX'"
        if ($afterX -eq 'x')       { "   => MODIFIER RELEASED at the request boundary" }
        elseif ($afterX -eq '')    { "   => MODIFIER STILL HELD (arrived as Ctrl+X, a cut)" }
        elseif ($afterX -like '*x') { "   => modifier released, but the box was not empty first" }
        else                       { "   => inconclusive, box holds '$afterX'" }

        # A second clear from a dirty box: the state every TestInit after the
        # first actually starts from, and where the residue was measured.
        Send $box '"residue"'
        Start-Sleep -Milliseconds 500
        Send $box '"\uE009a"'
        Start-Sleep -Milliseconds 500
        Send $box '"\uE017"'
        Start-Sleep -Milliseconds 700
        "5. second clear from dirty box  -> '$(TextOf $box)'   (REQUIRES empty)"

        try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 15 | Out-Null } catch { }
    }
    finally {
        Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

Probe 'WinAppDriver 1.2.1 (the reference)' 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe'
Probe 'WindowsDriverCore (ours)'           'C:\baseline\host\WindowsDriverCore.exe'

Get-Process Time -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
'=== probe complete ==='

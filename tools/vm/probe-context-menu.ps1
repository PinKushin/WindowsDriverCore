# Where does a context menu item live, and can WinAppDriver reach it?
#
# Runs INSIDE the guest's interactive session (queued via C:\baseline\cmd),
# because PowerShell Direct lands in session 0 and sees zero top-level windows
# through UIA.
#
# Two candidate mechanisms for Name=Delete never matching, and they need
# opposite fixes, so this measures which one is real rather than choosing:
#
#   A. the flyout is a XAML popup INSIDE the CoreWindow tree
#      -> it IS a descendant of the app window and our root or our walk is wrong
#   B. the flyout is a separate top-level HWND
#      -> no descendant search can reach it and a scoped popup fallback is needed
#
# The reference driver is asked the same question in the same run, per the
# standing rule that a mechanism WinAppDriver shares cannot explain a gap
# against it.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$ErrorActionPreference = 'Continue'
$base = 'http://127.0.0.1:4723'

function Post($path, $body) {
    Invoke-RestMethod -Method Post -Uri "$base$path" -Body ($body | ConvertTo-Json -Depth 6 -Compress) `
        -ContentType 'application/json' -TimeoutSec 30
}

'=== starting WinAppDriver (the reference) ==='
Get-Process WinAppDriver -ErrorAction SilentlyContinue | Stop-Process -Force
$srv = Start-Process -FilePath 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe' -PassThru
$up = $false
for ($i = 0; $i -lt 40; $i++) {
    try { $null = Invoke-RestMethod -Uri "$base/status" -TimeoutSec 5; $up = $true; break }
    catch { Start-Sleep -Seconds 1 }
}
"port 4723 answering: $up"
if (-not $up) { 'ABORT: driver never answered'; exit 1 }

try {
    $session = (Post '/session' @{ desiredCapabilities = @{ app = 'Microsoft.WindowsAlarms_8wekyb3d8bbwe!App' } }).sessionId
    "session: $session"
    Start-Sleep -Seconds 3

    function Find($using, $value) {
        try { (Post "/session/$session/element" @{ using = $using; value = $value }).value.ELEMENT }
        catch { $null }
    }

    # An alarm to right-click. Named distinctly so cleanup can find it and so it
    # cannot be confused with whatever the suite has left lying around.
    $probeName = 'ZZProbeAlarm'
    $add = Find 'accessibility id' 'AddAlarmButton'
    "AddAlarmButton: $add"
    if ($add) {
        Post "/session/$session/element/$add/click" @{} | Out-Null
        Start-Sleep -Seconds 2
        $box = Find 'accessibility id' 'AlarmNameTextBox'
        if ($box) {
            Post "/session/$session/element/$box/clear" @{} | Out-Null
            Post "/session/$session/element/$box/value" @{ value = @($probeName) } | Out-Null
        }
        $save = Find 'accessibility id' 'AlarmSaveButton'
        if (-not $save) { $save = Find 'accessibility id' 'PrimaryButton' }
        "save button: $save"
        if ($save) { Post "/session/$session/element/$save/click" @{} | Out-Null }
        Start-Sleep -Seconds 2
    }

    $entry = Find 'xpath' "//ListItem[starts-with(@Name, `"$probeName`")]"
    "probe ListItem: $entry"
    if (-not $entry) { 'ABORT: could not create or find the probe alarm'; }
    else {
        # Right-click it, exactly as AlarmClockBase.DeletePreviouslyCreatedAlarmEntry does.
        Post "/session/$session/moveto" @{ element = $entry } | Out-Null
        Post "/session/$session/click" @{ button = 2 } | Out-Null
        Start-Sleep -Seconds 3

        '=== Q1: can WINAPPDRIVER find it? ==='
        $delete = Find 'name' 'Delete'
        if ($delete) { "WinAppDriver FOUND Delete: element $delete" }
        else { 'WinAppDriver did NOT find Delete' }

        '=== Q2: where does Delete actually live? ==='
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $nameCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Delete')
        $hit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)

        if (-not $hit) {
            'Delete is NOT anywhere under the desktop root - the menu never opened'
        }
        else {
            "found: name='$($hit.Current.Name)' class=$($hit.Current.ClassName) type=$($hit.Current.ControlType.ProgrammaticName) pid=$($hit.Current.ProcessId)"

            # Walk to its top-level window. That ancestor is the whole answer:
            # if it is the app's own frame, the item is inside the app tree
            # (candidate A); if it is a separate popup, it never was (candidate B).
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $chain = @()
            $cur = $hit
            while ($cur -and $cur -ne $root) {
                $chain += "'$($cur.Current.Name)' [$($cur.Current.ClassName)] hwnd=$($cur.Current.NativeWindowHandle)"
                $cur = $walker.GetParent($cur)
            }
            'ancestor chain, innermost first:'
            $chain | ForEach-Object { "    $_" }

            $top = $chain[-1]
            "TOP-LEVEL OWNER: $top"
        }

        '=== Q3: which top-level windows exist right now? ==='
        $kids = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        "count: $($kids.Count)"
        foreach ($i in 0..($kids.Count - 1)) {
            $e = $kids[$i]
            if ($e.Current.ProcessId -eq $hit.Current.ProcessId -or $e.Current.Name -match 'Alarm') {
                "    '$($e.Current.Name)' [$($e.Current.ClassName)] pid=$($e.Current.ProcessId) hwnd=$($e.Current.NativeWindowHandle)"
            }
        }

        # Take the menu down so the probe does not leave the app in a modal state.
        try { Post "/session/$session/keys" @{ value = @([string][char]27) } | Out-Null } catch { }
    }

    # Remove the probe alarm rather than adding to the contamination this whole
    # investigation is about.
    try {
        Start-Sleep -Seconds 1
        $again = Find 'xpath' "//ListItem[starts-with(@Name, `"$probeName`")]"
        if ($again -and $delete) {
            Post "/session/$session/moveto" @{ element = $again } | Out-Null
            Post "/session/$session/click" @{ button = 2 } | Out-Null
            Start-Sleep -Seconds 2
            $d2 = Find 'name' 'Delete'
            if ($d2) { Post "/session/$session/element/$d2/click" @{} | Out-Null; 'probe alarm deleted' }
        }
    } catch { 'probe alarm cleanup failed' }

    try { Invoke-RestMethod -Method Delete -Uri "$base/session/$session" -TimeoutSec 15 | Out-Null } catch { }
}
finally {
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Get-Process 'Time' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
'=== probe complete ==='

# How long after the frame appears is the application's UI actually findable?
#
# MEASURED at 4b1c5da: POST /session returns the moment a window exists, and a
# find issued 26 ms later missed AppName in 6 of 19 attempts. The frame arrives
# before its content. This measures the gap so a wait budget can be chosen from
# data instead of picked.
#
# Reports three moments per launch:
#   t_frame     ApplicationFrameWindow for the app exists
#   t_children  that frame has ANY UIA child - content has begun to arrive
#   t_appname   the specific element the suite asks for is findable
#
# The number that matters is t_appname - t_frame: that is what the driver would
# have to wait, and it is what the client currently races.
#
# NOTE: on Windows 10 the Calculator process is named "Calculator", not
# "CalculatorApp" - an earlier probe cleaned up with the wrong name and left one
# running on the guest.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$ErrorActionPreference = 'Continue'

$AUT = @(
    @{ Name = 'Calculator'; AppId = 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'; Process = 'Calculator'; Title = 'Calculator';    Element = 'AppName'     },
    @{ Name = 'Alarms';     AppId = 'Microsoft.WindowsAlarms_8wekyb3d8bbwe!App';     Process = 'Time';       Title = 'Alarms & Clock'; Element = 'AlarmButton' }
)

function FrameFor($title) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $title)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function HasChildren($element) {
    if (-not $element) { return $false }
    try {
        $kids = $element.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        return $kids.Count -gt 0
    } catch { return $false }
}

function HasElement($element, $automationId) {
    if (-not $element) { return $false }
    try {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
        return $null -ne $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    } catch { return $false }
}

foreach ($app in $AUT) {
    "=========================================================="
    "  $($app.Name)  -  waiting for '$($app.Element)'"
    "=========================================================="

    for ($round = 1; $round -le 4; $round++) {
        Get-Process $app.Process -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 3

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Process 'explorer.exe' "shell:AppsFolder\$($app.AppId)"

        $tFrame = -1; $tChildren = -1; $tElement = -1
        $frame = $null

        while ($sw.ElapsedMilliseconds -lt 20000) {
            if ($tFrame -lt 0) {
                $frame = FrameFor $app.Title
                if ($frame) { $tFrame = $sw.ElapsedMilliseconds }
            }
            elseif ($tChildren -lt 0) {
                if (HasChildren $frame) { $tChildren = $sw.ElapsedMilliseconds }
            }
            elseif ($tElement -lt 0) {
                if (HasElement $frame $app.Element) { $tElement = $sw.ElapsedMilliseconds; break }
            }
            Start-Sleep -Milliseconds 10
        }
        $sw.Stop()

        if ($tFrame -lt 0) { "  round {0}: no frame within 20 s" -f $round; continue }

        $gapChildren = if ($tChildren -ge 0) { $tChildren - $tFrame } else { -1 }
        $gapElement  = if ($tElement  -ge 0) { $tElement  - $tFrame } else { -1 }

        "  round {0}: frame at {1,5} ms | children +{2,5} ms | '{3}' +{4,5} ms  <- THE GAP" -f `
            $round, $tFrame, $gapChildren, $app.Element, $gapElement
    }

    Get-Process $app.Process -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

""
"Read the last column: that is how long after the frame exists the element the"
"suite asks for becomes findable, and therefore how long a client currently"
"races when it searches immediately after POST /session returns."
'=== probe complete ==='

<#
.SYNOPSIS
    Runs the test suite inside the guest's INTERACTIVE desktop session.

.DESCRIPTION
    PowerShell Direct cannot do this, and that is not a configuration problem.
    Invoke-Command -VMName lands in a non-interactive session with no desktop, so
    every process it starts is windowless. Measured 2026-08-10 running the suite
    that way inside the guest:

        Unit         104 passed
        Protocol     111 passed
        Integration    7 passed, 7 FAILED, 104 skipped
                     "The test subject would not launch: Could not find
                      main window for application"

    Unit and Protocol pass because they never open a window. Everything that
    does either failed or skipped — including the Calculator fixtures, on a guest
    where Calculator is installed and present. A UI automation suite run over
    remoting measures nothing and says so quietly, in skips.

    So the run is handed to a scheduled task registered with /IT against the
    auto-logged-on account, which puts it on the real desktop. The host then
    polls for a sentinel file and reads the log back over PowerShell Direct —
    Copy-VMFile only moves host to guest, but reading a file through a remote
    session works in both directions.

.PARAMETER Filter
    A dotnet test --filter expression. Comparison is excluded by default because
    it drives real WinAppDriver and takes minutes.
#>
[CmdletBinding()]
param(
    [string] $VMName = "Win10-Baseline",
    [string] $Root = "F:\Hyper-V",
    [string] $RepositoryPath = "C:\baseline\WindowsDriverCore",
    [string] $Filter = "TestCategory!=Comparison",
    [int] $TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$credentialPath = Join-Path (Join-Path $Root $VMName) "credential.txt"
$lines = Get-Content -LiteralPath $credentialPath
$user = $lines[0]
$plainPassword = $lines[1]
$credential = [System.Management.Automation.PSCredential]::new(
    $user, (ConvertTo-SecureString $plainPassword -AsPlainText -Force))

# The script the scheduled task runs. Written on the host and pushed in, so the
# quoting is settled here rather than inside a schtasks command line.
$runner = @"
`$ErrorActionPreference = 'Continue'
Set-Location '$RepositoryPath'
`$env:Path += ';C:\dotnet;C:\Program Files\Git\cmd'
`$env:DOTNET_NOLOGO = 'true'
`$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'

Remove-Item C:\baseline\run.done -ErrorAction SilentlyContinue

& C:\dotnet\dotnet.exe test WindowsDriverCore.slnx --nologo -v q ``
    --filter '$Filter' *>&1 |
    Tee-Object -FilePath C:\baseline\run.log

# Written last, and only after the run: the host waits on this rather than on a
# clock, so a slow run is not mistaken for a finished one.
"done" | Set-Content C:\baseline\run.done
"@

$staging = Join-Path (Join-Path $Root $VMName) "push"
[System.IO.Directory]::CreateDirectory($staging) | Out-Null
$runnerPath = Join-Path $staging "run-tests.ps1"
[System.IO.File]::WriteAllText($runnerPath, $runner, (New-Object System.Text.UTF8Encoding $false))

Copy-VMFile -Name $VMName -SourcePath $runnerPath `
    -DestinationPath "C:\baseline\run-tests.ps1" -CreateFullPath -FileSource Host -Force

Write-Host "Starting the run on the guest's interactive desktop..."

Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $user, $plainPassword -ScriptBlock {
    param($user, $password)

    Remove-Item C:\baseline\run.done, C:\baseline\run.log -ErrorAction SilentlyContinue

    # schtasks rather than Register-ScheduledTask: /IT is what puts the task on
    # the interactive desktop, and the ScheduledTasks module has no equivalent
    # switch. /RU with /IT means "run as this user, in their session, only when
    # they are logged on" — which the answer file's AutoLogon guarantees.
    schtasks /create /tn WdcTests /f /sc once /st 00:00 /it `
        /ru $user /rp $password `
        /tr "powershell.exe -ExecutionPolicy Bypass -WindowStyle Normal -File C:\baseline\run-tests.ps1" | Out-Null

    schtasks /run /tn WdcTests | Out-Null
} | Out-Null

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true) {
    $done = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
        Test-Path C:\baseline\run.done
    }
    if ($done) { break }
    if ((Get-Date) -gt $deadline) {
        Write-Warning "Timed out after $TimeoutMinutes minutes. Partial log follows."
        break
    }
    Start-Sleep -Seconds 15
}

$log = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
    if (Test-Path C:\baseline\run.log) { Get-Content C:\baseline\run.log -Raw } else { "(no log)" }
}

Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
    schtasks /delete /tn WdcTests /f | Out-Null
} | Out-Null

$log

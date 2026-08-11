<#
.SYNOPSIS
    Provisions the baseline guest from staged files, with no network at all.

.DESCRIPTION
    THE INSTRUMENT MOVED WHILE IT WAS BEING USED. On 2026-08-10 the guest's
    Alarms & Clock updated itself from the Store, between the 13:53 WinAppDriver
    baseline run and the runs that resumed at 17:12. That update renamed
    AlarmSaveButton to PrimaryButton, removed AlarmNameTextBox and CancelButton,
    and took WinAppDriver's own score on that machine from 281/290 to 231/290.
    Fifty tests, with nothing in any log to say the subject had changed.

    A measuring instrument that updates itself is not an instrument. So this
    guest is provisioned with the network adapter DISCONNECTED and never
    reconnected, which means every payload has to arrive from the host.

    Copy-VMFile moves host to guest over the VMBus, not the network, so it keeps
    working with no adapter at all. So does PowerShell Direct, which is why the
    job-queue agent survives this.

.PARAMETER PayloadPath
    Staged installers. Build it with Save-OfflinePayload notes in the repo docs;
    the exact versions matter because they are the ones every prior measurement
    was taken with:

        dotnet-sdk-10.0.302-win-x64.zip        .NET SDK 10.0.302
        Git-2.47.1-64-bit.exe                  git 2.47.1
        WindowsApplicationDriver_1.2.1.msi     WinAppDriver 1.2.1 (file 1.2.2009.02003)
        microsoft.testplatform.17.12.0.nupkg   vstest 17.1200.24.56501
        WebDriverAPI.zip                       the compiled compatibility suite
        WindowsDriverCore.bundle               this repository, as a git bundle

.PARAMETER Force
    Provision even though the network adapter is connected. There is one honest
    reason to pass this and it is not convenience: you have deliberately chosen a
    host-only switch and want a git remote. Anything else reintroduces the exact
    failure this script exists to prevent.
#>
[CmdletBinding()]
param(
    [string] $VMName = "Win10-Baseline",
    [string] $Root = "F:\Hyper-V",
    [string] $PayloadPath = "F:\Hyper-V\offline-payload",
    [switch] $Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$required = @(
    "dotnet-sdk-10.0.302-win-x64.zip",
    "Git-2.47.1-64-bit.exe",
    "WindowsApplicationDriver_1.2.1.msi",
    "microsoft.testplatform.17.12.0.nupkg",
    "WebDriverAPI.zip",
    "WindowsDriverCore.bundle")

$missing = $required | Where-Object { -not (Test-Path (Join-Path $PayloadPath $_)) }
if ($missing) {
    throw "Payload incomplete. Missing from '$PayloadPath': $($missing -join ', ')"
}

# THE CHECK THAT MATTERS. A connected adapter means the Store can reach the
# internet, and this whole script exists because it did.
$adapter = Get-VM -Name $VMName | Get-VMNetworkAdapter
if ($adapter.Connected -and -not $Force) {
    throw @"
'$VMName' has a CONNECTED network adapter ($($adapter.SwitchName)).

That is how the last guest's Alarms & Clock updated itself mid-investigation and
cost fifty compatibility tests. Disconnect it first:

    Get-VM $VMName | Get-VMNetworkAdapter | Disconnect-VMNetworkAdapter

PowerShell Direct and Copy-VMFile both ride the VMBus, so nothing here needs it.
"@
}

$credentialPath = Join-Path (Join-Path $Root $VMName) "credential.txt"
$lines = Get-Content -LiteralPath $credentialPath
$credential = [System.Management.Automation.PSCredential]::new(
    $lines[0], (ConvertTo-SecureString $lines[1] -AsPlainText -Force))

Write-Host "Pushing payload to the guest (283 MB of it, so this takes a while)..."

# What is already on the guest, so a re-run does not re-push a quarter of a
# gigabyte. Every INSTALL step below is guarded by Test-Path and this one was
# not, which made the script idempotent in effect and expensive in practice.
#
# Compared by length rather than by hash: Copy-VMFile is a byte copy over the
# VMBus with no transform, so a matching size is sufficient - and hashing
# 283 MB inside the guest to avoid copying 283 MB is not a saving.
$already = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
    $map = @{}
    Get-ChildItem 'C:\baseline\payload' -File -ErrorAction SilentlyContinue |
        ForEach-Object { $map[$_.Name] = $_.Length }
    $map
}

foreach ($file in $required + @("dotnet-install.ps1")) {
    $source = Join-Path $PayloadPath $file
    if (-not (Test-Path $source)) { continue }
    $size = (Get-Item $source).Length
    if ($already -and $already.ContainsKey($file) -and $already[$file] -eq $size) {
        Write-Host "  $file (already there)"
        continue
    }


    Write-Host "  $file"
    Copy-VMFile -Name $VMName -SourcePath $source `
        -DestinationPath "C:\baseline\payload\$file" -CreateFullPath -FileSource Host -Force
}

# The agent comes from the repository rather than the payload, because it is
# source rather than a pinned third-party binary and should follow the branch
# being measured.
$agent = Join-Path $PSScriptRoot "agent.ps1"
if (Test-Path $agent) {
    Write-Host "  agent.ps1"
    Copy-VMFile -Name $VMName -SourcePath $agent `
        -DestinationPath "C:\baseline\agent.ps1" -CreateFullPath -FileSource Host -Force
}

Write-Host "Installing on the guest..."
Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
    $ErrorActionPreference = 'Continue'
    $report = [System.Collections.Generic.List[string]]::new()
    $payload = 'C:\baseline\payload'

    # BELT AND BRACES. The adapter is disconnected, but a policy costs nothing
    # and survives someone reconnecting it later to copy one file.
    $policy = 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore'
    New-Item -Path $policy -Force | Out-Null
    Set-ItemProperty -Path $policy -Name 'AutoDownload' -Value 2 -Type DWord
    Set-ItemProperty -Path $policy -Name 'DisableOSUpgrade' -Value 1 -Type DWord
    $report.Add('store auto-update : disabled by policy')

    if (-not (Test-Path 'C:\dotnet\dotnet.exe')) {
        Expand-Archive -Path "$payload\dotnet-sdk-10.0.302-win-x64.zip" -DestinationPath 'C:\dotnet' -Force
        $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
        if ($machine -notlike '*C:\dotnet*') {
            [Environment]::SetEnvironmentVariable('Path', "$machine;C:\dotnet", 'Machine')
        }
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', 'C:\dotnet', 'Machine')
    }
    $env:Path += ';C:\dotnet'
    $env:DOTNET_ROOT = 'C:\dotnet'

    if (-not (Test-Path 'C:\Program Files\Git\cmd\git.exe')) {
        Start-Process "$payload\Git-2.47.1-64-bit.exe" -Wait -ArgumentList '/VERYSILENT', '/NORESTART'
    }
    $env:Path += ';C:\Program Files\Git\cmd'

    if (-not (Test-Path 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe')) {
        Start-Process msiexec.exe -Wait -ArgumentList `
            '/i', "$payload\WindowsApplicationDriver_1.2.1.msi", '/quiet', '/norestart'
    }

    # A nupkg is a zip. Renaming rather than adding a NuGet client keeps this
    # offline and keeps the version pinned to the one every prior score used.
    if (-not (Test-Path 'C:\baseline\testplatform')) {
        Copy-Item "$payload\microsoft.testplatform.17.12.0.nupkg" 'C:\baseline\testplatform.zip' -Force
        Expand-Archive -Path 'C:\baseline\testplatform.zip' -DestinationPath 'C:\baseline\testplatform' -Force
    }

    if (-not (Test-Path 'C:\baseline\WebDriverAPI')) {
        Expand-Archive -Path "$payload\WebDriverAPI.zip" -DestinationPath 'C:\baseline\WebDriverAPI' -Force
    }

    # Cloned FROM THE BUNDLE, which is a complete repository in one file. The
    # remote is left pointing at it so a later push of a fresh bundle updates the
    # guest without a network.
    if (-not (Test-Path 'C:\baseline\WindowsDriverCore\.git')) {
        & 'C:\Program Files\Git\cmd\git.exe' clone "$payload\WindowsDriverCore.bundle" `
            'C:\baseline\WindowsDriverCore' 2>&1 | Out-Null
    }

    # THE CLONE IS OWNED BY ADMINISTRATORS, because this provisioning session is
    # elevated, and the agent that runs the suite is not. git refuses a repository
    # owned by somebody else - "detected dubious ownership" - and the failure is
    # nastier than it looks: the error text ends up in $head and becomes the .trx
    # filename, which vstest then rejects as an invalid logger URI, so a run
    # completes with no results and exit code 0.
    #
    # Elevated and non-elevated tester share one profile, so a --global setting
    # written here is the one the agent reads.
    & 'C:\Program Files\Git\cmd\git.exe' config --global --add safe.directory 'C:/baseline/WindowsDriverCore' 2>&1 | Out-Null
    $report.Add('git safe.directory : set for the agent')

    $report.Add('--- versions, which must match what prior scores were taken with ---')
    $report.Add('dotnet       : ' + (& C:\dotnet\dotnet.exe --version 2>&1))
    $report.Add('git          : ' + ((& 'C:\Program Files\Git\cmd\git.exe' --version 2>&1) -join ''))
    $report.Add('winappdriver : ' + $(
        if (Test-Path 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe') {
            (Get-Item 'C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe').VersionInfo.FileVersion
        } else { 'MISSING' }))
    $report.Add('vstest       : ' + $(
        (Get-ChildItem 'C:\baseline\testplatform' -Filter 'vstest.console.exe' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1).VersionInfo.FileVersion))

    $report.Add('--- THE NUMBER THIS REBUILD WAS FOR ---')
    $report.Add('windows      : build ' + (Get-CimInstance Win32_OperatingSystem).BuildNumber)
    $report.Add('alarms       : ' + (Get-AppxPackage Microsoft.WindowsAlarms).Version +
        '   (the drifted guest was 11.2606.11.0, and scored 231/290 for WinAppDriver)')
    $report.Add('calculator   : ' + (Get-AppxPackage Microsoft.WindowsCalculator).Version)
    $report.Add('internet     : ' + (Test-NetConnection -ComputerName 8.8.8.8 -Port 443 `
        -InformationLevel Quiet -WarningAction SilentlyContinue) + '   (must be False)')

    $report
} | ForEach-Object { Write-Host $_ }

# ---------------------------------------------------------------------------
# The agent, started by the machine rather than by a person.
#
# It has to run in the INTERACTIVE session: PowerShell Direct lands in session 0,
# which has no desktop, and a UI automation job started there produces windowless
# processes and a run that measures nothing while reporting skips.
#
# /IT is what puts a task on the interactive desktop, and the ScheduledTasks
# module has no equivalent switch - hence schtasks. It CANNOT be started from
# session 0 on demand (`schtasks /run` answers "ERROR: Element not found",
# because Task Scheduler cannot resolve the interactive token across the session
# boundary), so the trigger is logon and the guest is restarted to fire it. The
# answer file's AutoLogon then brings the agent up unattended, on this boot and
# every boot after it.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Installing the agent as a Startup item..."

Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
    # THE STARTUP FOLDER, NOT A SCHEDULED TASK.
    #
    # schtasks /create /sc onlogon /it /ru <user> /rp <password> warned "not all
    # specified triggers will start the task" and then did not persist the task at
    # all - it was absent afterwards from both schtasks /query and
    # Get-ScheduledTask. /IT means "run only while this user is logged on", which
    # is answered by the interactive token; supplying a stored password as well is
    # contradictory.
    #
    # The Startup folder runs in the interactive session at logon by definition.
    # No password, no trigger semantics, nothing to misregister.
    #
    # Task Scheduler /IT is still right where Invoke-GuestTests.ps1 uses it, for a
    # different problem: STARTING something on the interactive desktop on demand
    # from session 0. That is not this.
    $startup = Join-Path $env:APPDATA '\Microsoft\Windows\Start Menu\Programs\Startup'
    $cmd = Join-Path $startup 'wdc-agent.cmd'

    Set-Content -LiteralPath $cmd -Encoding Ascii -Value @(
        '@echo off',
        'rem Starts the job-queue agent in the interactive session at logon.',
        'start "" powershell.exe -ExecutionPolicy Bypass -NoExit -File C:\baseline\agent.ps1'
    )

    'startup item: ' + (Test-Path $cmd)
} | Write-Host

# NOTE ON ELEVATION. The Startup item runs NON-elevated, which is the steady
# state every future boot will have. An agent started by hand from an elevated
# console is a different instrument, and a baseline measured there is measured
# under conditions that will not recur - the same unmatched-conditions mistake
# that made the 281 and 231 scores incomparable. Reboot before the first
# baseline run so the agent comes from here.

Write-Host "Restarting the guest so the logon trigger fires..."
Restart-VM -Name $VMName -Force -Wait -For Heartbeat

Write-Host "Waiting for the agent's heartbeat..."
$deadline = (Get-Date).AddMinutes(5)
$beat = $null
while ((Get-Date) -lt $deadline -and -not $beat) {
    Start-Sleep -Seconds 10
    $beat = Invoke-Command -VMName $VMName -Credential $credential -ErrorAction SilentlyContinue -ScriptBlock {
        if (Test-Path 'C:\baseline\cmd\_agent.heartbeat') {
            Get-Content 'C:\baseline\cmd\_agent.heartbeat' -Raw
        }
    }
}

if ($beat) {
    Write-Host "agent: $beat"
} else {
    Write-Warning "No agent heartbeat after five minutes. Check the guest's desktop -"
    Write-Warning "a live process with a dead loop looks identical from session 0, which"
    Write-Warning "is why the heartbeat file exists."
}

Write-Host ""
Write-Host "CHECKPOINT IT NOW, before anything else touches the guest."
Write-Host ""
Write-Host "  Checkpoint-VM -Name $VMName -SnapshotName ""fresh-alarms-<version>"""
Write-Host ""
Write-Host "The previous guest was lost to an app update and had ZERO checkpoints,"
Write-Host "no restore points and System Protection off - measured 2026-08-11, all"
Write-Host "three. None of them would have helped anyway: Store app updates do not"
Write-Host "trigger restore points, and System Restore does not cover packages under"
Write-Host "C:\Program Files\WindowsApps. A Hyper-V checkpoint does, because it"
Write-Host "snapshots the whole disk. It is the only thing that would have saved the"
Write-Host "281 image, and it costs a few hundred megabytes."
Write-Host ""
Write-Host "Then: start the agent on the guest's interactive desktop, and run"
Write-Host "Invoke-CompatibilitySuite.ps1 -Driver WinAppDriver to establish this"
Write-Host "image's ceiling. Do not compare it to 281 or 231 without quoting the"
Write-Host "Alarms version above - those two were measured against different apps."

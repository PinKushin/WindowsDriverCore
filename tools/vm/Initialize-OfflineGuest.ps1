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
foreach ($file in $required + @("dotnet-install.ps1")) {
    $source = Join-Path $PayloadPath $file
    if (-not (Test-Path $source)) { continue }

    Write-Host "  $file"
    Copy-VMFile -Name $VMName -SourcePath $source `
        -DestinationPath "C:\baseline\payload\$file" -CreateFullPath -FileSource Host -Force
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

Write-Host ""
Write-Host "Next: start the agent on the guest's interactive desktop, then run"
Write-Host "Invoke-CompatibilitySuite.ps1 -Driver WinAppDriver to establish this"
Write-Host "image's ceiling. Do not compare it to 281 or 231 without quoting the"
Write-Host "Alarms version above - those two were measured against different apps."

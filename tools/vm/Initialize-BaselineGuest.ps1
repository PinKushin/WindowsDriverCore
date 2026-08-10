<#
.SYNOPSIS
    Installs the toolchain inside the baseline VM and proves it can measure.

.DESCRIPTION
    Everything runs over PowerShell Direct — Invoke-Command -VMName — which goes
    through the hypervisor's guest interface rather than the network. No RDP, no
    WinRM configuration, no firewall rules, and it keeps working if the guest's
    networking is broken, which matters because a half-provisioned guest is
    exactly when networking tends to be broken.

    Safe to re-run. Every step checks for what it installs first, so a failure
    part way through is fixed by running it again rather than by rebuilding the
    VM.

.PARAMETER VMName
    The VM created by New-BaselineVm.ps1.

.EXAMPLE
    .\Initialize-BaselineGuest.ps1 -VMName Win10-Baseline
#>
[CmdletBinding()]
param(
    [string] $VMName = "Win10-Baseline",

    [string] $Root = "F:\Hyper-V",

    # Public repository, so the guest clones it rather than having a working
    # tree copied in. Copy-VMFile would also work and is the fallback if the
    # guest has no network.
    [string] $RepositoryUrl = "https://github.com/PinKushin/WindowsDriverCore.git"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
if (-not $vm) { throw "No VM named '$VMName'. Run New-BaselineVm.ps1 first." }
if ($vm.State -ne "Running") { throw "'$VMName' is $($vm.State). Start it first." }

$credentialPath = Join-Path (Join-Path $Root $VMName) "credential.txt"
if (-not (Test-Path -LiteralPath $credentialPath)) {
    throw "No credential at '$credentialPath'. It is written by New-BaselineVm.ps1."
}

$lines = Get-Content -LiteralPath $credentialPath
$credential = [System.Management.Automation.PSCredential]::new(
    $lines[0],
    (ConvertTo-SecureString $lines[1] -AsPlainText -Force))

# ---------------------------------------------------------------------------
# Wait for the guest, on the condition rather than on the clock.
#
# Windows Setup reboots several times and PowerShell Direct only answers once
# the guest is at a logged-on desktop. Polling for that is the synchronisation
# point; a sleep long enough to "usually" cover it is how a provisioning script
# becomes flaky.
# ---------------------------------------------------------------------------
Write-Host "Waiting for the guest to answer PowerShell Direct..."

$deadline = (Get-Date).AddMinutes(45)
while ($true) {
    try {
        $null = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock { 1 } -ErrorAction Stop
        break
    }
    catch {
        if ((Get-Date) -gt $deadline) {
            throw "The guest never answered. Watch it: vmconnect.exe localhost '$VMName'"
        }
        Start-Sleep -Seconds 15
    }
}

Write-Host "Guest is up."

$report = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
    $ErrorActionPreference = "Stop"
    $log = [System.Collections.Generic.List[string]]::new()

    function Step([string] $name, [scriptblock] $already, [scriptblock] $install) {
        if (& $already) { $log.Add("$name : already present"); return }
        & $install
        $log.Add("$name : installed")
    }

    # This is the whole reason for the VM. If it is not Windows 10 the
    # measurement is meaningless, so it fails here rather than producing a
    # number nobody can interpret later.
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os.Caption -notmatch "Windows 10") {
        throw "Guest is '$($os.Caption)', not Windows 10. The whole point is the Windows 10 applications."
    }
    $log.Add("os : $($os.Caption) build $($os.BuildNumber)")

    # The inbox applications the compatibility suite was written against. Their
    # presence is the thing Server runners lack and Windows 11 changed, so it is
    # recorded rather than assumed.
    $calculator = Get-AppxPackage -Name "Microsoft.WindowsCalculator" -ErrorAction SilentlyContinue
    $log.Add("calculator : " + $(if ($calculator) { $calculator.Version } else { "ABSENT — the guest cannot host the suite" }))
    $log.Add("notepad : " + $(if (Test-Path "$env:WINDIR\System32\notepad.exe") { "present" } else { "ABSENT" }))

    # Developer Mode is set by the answer file; WinAppDriver's installer wants
    # it and silently misbehaves without it.
    $unlock = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue
    $log.Add("developer mode : " + $(if ($unlock -and $unlock.AllowDevelopmentWithoutDevLicense -eq 1) { "on" } else { "OFF" }))

    New-Item -ItemType Directory -Path "C:\baseline" -Force | Out-Null

    Step ".NET SDK" { [bool](Get-Command dotnet -ErrorAction SilentlyContinue) } {
        # Microsoft's own installer script, from Microsoft's own domain. It puts
        # the SDK in the user profile, so no elevation and no MSI.
        Invoke-WebRequest -UseBasicParsing `
            -Uri "https://dot.net/v1/dotnet-install.ps1" `
            -OutFile "C:\baseline\dotnet-install.ps1"
        & "C:\baseline\dotnet-install.ps1" -Channel 10.0 -InstallDir "C:\dotnet"
        $machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
        [Environment]::SetEnvironmentVariable("Path", "$machine;C:\dotnet", "Machine")
        $env:Path += ";C:\dotnet"
    }

    Step "git" { [bool](Get-Command git -ErrorAction SilentlyContinue) } {
        # winget is not on a fresh 22H2 image, so this is the release MSI.
        $git = "https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/Git-2.47.1-64-bit.exe"
        Invoke-WebRequest -UseBasicParsing -Uri $git -OutFile "C:\baseline\git-setup.exe"
        Start-Process "C:\baseline\git-setup.exe" -Wait -ArgumentList "/VERYSILENT", "/NORESTART"
        $env:Path += ";C:\Program Files\Git\cmd"
    }

    Step "WinAppDriver" { Test-Path "C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe" } {
        # The baseline being measured. Archived June 2025, so this version is
        # the last one there will ever be.
        $wad = "https://github.com/microsoft/WinAppDriver/releases/download/v1.2.1/WindowsApplicationDriver_1.2.1.msi"
        Invoke-WebRequest -UseBasicParsing -Uri $wad -OutFile "C:\baseline\winappdriver.msi"
        Start-Process msiexec.exe -Wait -ArgumentList "/i", "C:\baseline\winappdriver.msi", "/quiet", "/norestart"
    }

    $log.Add("--- versions ---")
    $log.Add("dotnet : " + (& dotnet --version 2>&1))
    $log.Add("git : " + ((& git --version 2>&1) -join ""))
    $log.Add("winappdriver : " + $(if (Test-Path "C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe") { "installed" } else { "MISSING" }))

    $log
}

$report | ForEach-Object { Write-Host "  $_" }

Write-Host ""
Write-Host "Guest provisioned. Clone and measure with:"
Write-Host "  Invoke-Command -VMName '$VMName' -Credential `$credential -ScriptBlock {"
Write-Host "      git clone $RepositoryUrl C:\baseline\WindowsDriverCore"
Write-Host "      cd C:\baseline\WindowsDriverCore; dotnet test WindowsDriverCore.slnx"
Write-Host "  }"
Write-Host ""
Write-Host "The WinAppDriver compatibility suite lives in the sibling repository and"
Write-Host "is NOT cloned here. Copy the built WebDriverAPI.dll in with Copy-VMFile,"
Write-Host "so the guest measures the same binary this desktop measured."

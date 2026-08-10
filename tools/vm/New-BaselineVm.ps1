<#
.SYNOPSIS
    Creates the Windows 10 baseline VM and starts a hands-off install.

.DESCRIPTION
    The comparison numbers in this repository are unmatched. WinAppDriver's
    112/290 was measured on Windows 11, whose Calculator, Notepad and Settings
    are not the applications the compatibility suite was written against, so a
    large share of those failures are app drift rather than driver capability.
    Measuring both drivers inside a Windows 10 guest, in one session, is what
    makes the comparison about the drivers.

    Everything here runs unelevated: the account is in Hyper-V Administrators,
    which is what the Hyper-V cmdlets actually check.

.PARAMETER IsoPath
    A Windows 10 installation ISO. Not downloaded by this script.

.EXAMPLE
    .\New-BaselineVm.ps1 -IsoPath C:\Users\pinku\Downloads\Win10_22H2.iso
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $IsoPath,

    [string] $VMName = "Win10-Baseline",

    # F: has the space. C: is the Hyper-V default and has 133 GB, which a
    # dynamic 80 GB disk plus a checkpoint can eat into uncomfortably.
    [string] $Root = "F:\Hyper-V",

    [int] $ProcessorCount = 4,
    [int64] $StartupMemory = 4GB,
    [int64] $MaximumMemory = 8GB,
    [int64] $DiskSize = 80GB
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $IsoPath)) {
    throw "No ISO at '$IsoPath'."
}

if (Get-VM -Name $VMName -ErrorAction SilentlyContinue) {
    throw "A VM named '$VMName' already exists. Remove it first, deliberately."
}

$vmRoot = Join-Path $Root $VMName
$null = New-Item -ItemType Directory -Path $vmRoot -Force

# ---------------------------------------------------------------------------
# The answer file, delivered on its own ISO.
#
# Windows Setup looks for autounattend.xml at the root of attached media. The
# obvious alternative — a second VHDX — is a trap: the answer file wipes DiskID
# 0, and if the guest enumerates the two disks the other way round the install
# targets the wrong one. A DVD cannot be mistaken for an install target.
#
# No oscdimg on this machine (no Windows ADK), so the ISO is authored through
# IMAPI2, which ships with Windows.
# ---------------------------------------------------------------------------

# A password is required for AutoLogon, and AutoLogon is required because a UI
# automation test needs a real interactive desktop. Generated per VM rather than
# hardcoded, so nothing in the repository is a working credential.
$password = -join ((1..20) | ForEach-Object {
    [char[]]"ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789" | Get-Random
})

$unattendSource = Join-Path $PSScriptRoot "autounattend.xml"
$unattendStaging = Join-Path $vmRoot "unattend-staging"
$null = New-Item -ItemType Directory -Path $unattendStaging -Force

(Get-Content -LiteralPath $unattendSource -Raw).Replace("__PASSWORD__", $password) |
    Set-Content -LiteralPath (Join-Path $unattendStaging "autounattend.xml") -Encoding UTF8

$answerIso = Join-Path $vmRoot "autounattend.iso"

Write-Host "Authoring $answerIso"

$fs = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$fs.FileSystemsToCreate = 3          # ISO9660 + Joliet
$fs.VolumeName = "UNATTEND"
$fs.Root.AddTree($unattendStaging, $false)

$result = $fs.CreateResultImage()

# The cast to IStream has to happen in C#, and that is not a style choice.
# PowerShell 7 binds COM objects late: casting IMAPI2's ImageStream to
# System.Runtime.InteropServices.ComTypes.IStream appears to succeed and then
# fails with "does not contain a method named 'Read'", because what comes back
# is still a __ComObject. Both were measured. In C# the runtime-callable
# wrapper is typed and the cast is real.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class IsoWriter
{
    public static long Write(object imageStream, string path)
    {
        IStream stream = (IStream)imageStream;
        long total = 0;
        byte[] buffer = new byte[2048 * 64];

        // pcbRead is declared IntPtr by the ComTypes signature, so it needs
        // unmanaged memory rather than an out parameter.
        IntPtr read = Marshal.AllocHGlobal(4);
        try
        {
            using (FileStream file = File.Create(path))
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, read);
                    int count = Marshal.ReadInt32(read);
                    if (count <= 0) { break; }
                    file.Write(buffer, 0, count);
                    total += count;
                }
            }
        }
        finally { Marshal.FreeHGlobal(read); }
        return total;
    }
}
'@ -ErrorAction SilentlyContinue

$written = [IsoWriter]::Write($result.ImageStream, $answerIso)
Write-Host ("  {0:N0} bytes" -f $written)

Remove-Item -LiteralPath $unattendStaging -Recurse -Force

# ---------------------------------------------------------------------------
# The VM itself.
# ---------------------------------------------------------------------------

$vhdPath = Join-Path $vmRoot "$VMName.vhdx"

Write-Host "Creating $VMName"

# Generation 2 for UEFI, which is what the answer file's GPT layout assumes.
$vm = New-VM -Name $VMName -Generation 2 -MemoryStartupBytes $StartupMemory `
    -NewVHDPath $vhdPath -NewVHDSizeBytes $DiskSize -SwitchName "Default Switch"

Set-VM -VM $vm -ProcessorCount $ProcessorCount -AutomaticCheckpointsEnabled $false
Set-VMMemory -VM $vm -DynamicMemoryEnabled $true `
    -MinimumBytes 2GB -StartupBytes $StartupMemory -MaximumBytes $MaximumMemory

# Windows 10 boots fine under Secure Boot with the Microsoft template.
Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows

Add-VMDvdDrive -VM $vm -Path $IsoPath
Add-VMDvdDrive -VM $vm -Path $answerIso

# Boot the installer, not the empty disk.
$installDvd = Get-VMDvdDrive -VM $vm | Where-Object { $_.Path -eq $IsoPath }
Set-VMFirmware -VM $vm -FirstBootDevice $installDvd

# Integration services: PowerShell Direct needs the guest interface, and that is
# how everything after this point is driven — no RDP, no network dependency.
Enable-VMIntegrationService -VM $vm -Name "Guest Service Interface"

Start-VM -VM $vm

$credentialPath = Join-Path $vmRoot "credential.txt"
"tester`n$password" | Set-Content -LiteralPath $credentialPath -Encoding UTF8

Write-Host ""
Write-Host "Started. The install is unattended and takes roughly 15 minutes."
Write-Host "Account 'tester', password written to $credentialPath"
Write-Host ""
Write-Host "Watch it:      vmconnect.exe localhost '$VMName'"
Write-Host "When it idles: .\Initialize-BaselineGuest.ps1 -VMName '$VMName'"

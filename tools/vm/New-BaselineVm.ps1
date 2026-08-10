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
# automation test needs a real interactive desktop.
#
# SHORT AND FIXED, DELIBERATELY. This was a generated 20-character string, and
# that was the wrong trade twice over:
#
#  - It bought nothing. The password has to be readable by the host to drive
#    PowerShell Direct, so it sits in plaintext in credential.txt and inside the
#    answer ISO regardless. A long random string that lives in a file next to the
#    VHDX is not more secret than a short one, only harder to type.
#  - It has to be typed by hand when something goes wrong, and something did:
#    the answer file's password did not take on the first build (see below), so
#    recovery meant reading 20 random characters off the host and typing them
#    into a VM console that has no clipboard in basic session.
#
# The guest is a disposable measurement rig on an internal switch with no inbound
# exposure and no data on it. If that ever stops being true, this is the line to
# revisit.
#
# KNOWN DEFECT, unresolved: on the first build the account and computer name from
# the answer file applied correctly but this password did not — every credential
# form was rejected by PowerShell Direct, while credential.txt and the booted
# ISO's XML were byte-identical and the string needed no XML escaping. Worth
# testing whether LocalAccount wants base64 with PlainText=false on 22H2. Until
# then the recovery is `net user tester <password>` from an elevated prompt in
# the guest.
$password = "test"

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

# SECURE BOOT OFF, and it has to be. Measured 2026-08-10: with the
# MicrosoftWindows template the firmware reported
#
#     1. SCSI DVD (0,1)   The boot loader failed.
#
# "failed", not "not found" — the firmware reached the ISO and refused its
# loader. The ISO is fine: bootx64.efi, cdboot.efi and bootmgr.efi are all
# present and it is a stock Media Creation Tool image.
#
# The cause is the BlackLotus revocations (KB5025885). Windows 10 22H2 media
# carries the pre-revocation bootmgr, the updated DBX blocklists it, and a
# Windows 11 host enforces that. So EVERY Windows 10 installer fails Secure Boot
# on a current host — this is not something a different ISO fixes.
#
# Acceptable here because the guest is a disposable measurement rig on an
# internal switch. It would not be acceptable for anything that mattered.
Set-VMFirmware -VM $vm -EnableSecureBoot Off

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

Start-Process vmconnect.exe -ArgumentList "localhost", $VMName -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "  >>> PRESS A KEY IN THE VM WINDOW WHEN IT SAYS 'Press any key to" -ForegroundColor Yellow
Write-Host "  >>> boot from CD or DVD'. It appears about 10 seconds in and lasts" -ForegroundColor Yellow
Write-Host "  >>> about 5 seconds. Miss it and the VM falls through to PXE." -ForegroundColor Yellow
Write-Host ""
# This cannot be automated from here. Msvm_Keyboard's TypeKey is deprecated: it
# returns success and does nothing on a current host, measured across 40
# keypresses that changed the screen not at all. Removing the prompt instead
# means repacking the ISO with efisys_noprompt.bin, which needs oscdimg from the
# Windows ADK — not installed, and a large dependency for one keystroke.
#
# Everything after this keystroke is unattended.
Write-Host "That keystroke is the only manual step. The install then runs"
Write-Host "unattended for roughly 15 minutes."
Write-Host ""
Write-Host "Account 'tester', password written to $credentialPath"
Write-Host "When it idles at the desktop: .\Initialize-BaselineGuest.ps1 -VMName '$VMName'"

<#
.SYNOPSIS
    Runs a local probe script inside the Win10 guest's DESKTOP session and
    returns its output.

.DESCRIPTION
    PowerShell Direct lands in session 0, which has no desktop - so anything
    that drives an application, injects input or reads a UIA tree sees nothing
    there. The guest agent watches C:\baseline\cmd and runs queued scripts in
    session 1, which is the only place a probe means anything.

    Invoke-CompatibilitySuite.ps1 has always done this for the suite. Every
    ad-hoc probe re-derived it, so it is a function now.

    THE FAILURE VOCABULARY IS NOT FILTERED HERE, deliberately. A run that
    aborted has been mistaken for a run in progress in this project, because the
    caller's Select-String matched only the success words. This returns
    everything and lets the caller decide.

.PARAMETER Script
    Path to the local .ps1 to run in the guest.

.PARAMETER TimeoutMinutes
    How long to wait for the .done marker.

.EXAMPLE
    .\Invoke-GuestProbe.ps1 -Script .\probe-how-far-does-a-scroll-go.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Script,
    [string] $VMName = 'Win10-Baseline',
    [string] $Root = 'F:\Hyper-V',
    [int] $TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Script)) { throw "No such script: $Script" }
$text = Get-Content -LiteralPath $Script -Raw

$lines = Get-Content -LiteralPath (Join-Path (Join-Path $Root $VMName) 'credential.txt')
$credential = [System.Management.Automation.PSCredential]::new(
    $lines[0], (ConvertTo-SecureString $lines[1] -AsPlainText -Force))

$name = "probe-$(Get-Date -Format HHmmss)"

Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $text, $name -ScriptBlock {
    param($body, $jobName)
    [System.IO.File]::WriteAllText("C:\baseline\cmd\$jobName.ps1", $body)
}
Write-Host "queued $name ($(Split-Path $Script -Leaf))"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    $log = Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $name -ScriptBlock {
        param($jobName)
        if (Test-Path "C:\baseline\cmd\$jobName.done") { Get-Content "C:\baseline\cmd\$jobName.log" -Raw } else { $null }
    }

    if ($log) { Write-Host $log; return }
    Start-Sleep -Seconds 10
}

# THROWS RATHER THAN RETURNING EMPTY. A probe that timed out and a probe that
# printed nothing are different things, and this project has already spent a
# night reading one as the other.
throw "Probe $name did not finish within $TimeoutMinutes minutes."

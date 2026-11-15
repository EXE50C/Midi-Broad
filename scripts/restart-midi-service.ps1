# Restarts the Windows MIDI service. Run in an elevated PowerShell.
# This is only needed if the MIDI Services preview gets stuck; a reboot is the
# most reliable recovery.

$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (Run as administrator)."
}

Restart-Service midisrv -Force
Get-Service midisrv | Select-Object Name, Status

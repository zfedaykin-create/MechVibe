# Copies the built plugin into the SimHub installation folder.
#
# SimHub loads plugin DLLs from its own directory, so this needs write access to
# Program Files - run it from an elevated shell if it fails on permissions.
# SimHub must be closed: it locks loaded plugin assemblies.

$ErrorActionPreference = "Stop"

$dll    = Join-Path $PSScriptRoot "bin\User.MechVibe.dll"
$simhub = "C:\Program Files (x86)\SimHub"

if (-not (Test-Path $dll))    { throw "Plugin not built yet - run build.ps1 first." }
if (-not (Test-Path $simhub)) { throw "SimHub not found at $simhub" }

$running = Get-Process -Name "SimHub*" -ErrorAction SilentlyContinue
if ($running) {
    throw "SimHub is running (PID $($running.Id -join ',')). Close it first - it holds plugin DLLs open."
}

$dest = Join-Path $simhub "User.MechVibe.dll"
Copy-Item $dll $dest -Force

Write-Host "Installed: $dest"
Write-Host ""
Write-Host "Next: start SimHub. It will ask whether to enable the new plugin"
Write-Host "('MechWarrior 5: Clans Telemetry') - say yes, then find the properties"
Write-Host "under MW5Clans.* when building ShakeIt effects."

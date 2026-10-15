<#
.SYNOPSIS
  Installs (or updates) Monitor v2 on this PC. Run in an administrator PowerShell.

.DESCRIPTION
  - copies the program to C:\Program Files\MonitorV2 (users can run it, not change it)
  - enrolls the PC with a one-time code from the web app (PCs -> Add PC); the device token is kept in
    C:\ProgramData\MonitorV2, readable only by SYSTEM and Administrators
  - registers the "MonitorV2" Windows service (LocalSystem, automatic start, restarted if it stops)
  The service then starts the tray app in every standard user's session. Administrator accounts are not monitored.

.PARAMETER ServerUrl
  The device Worker, e.g. https://monitor-device.<your-subdomain>.workers.dev

.PARAMETER Code
  One-time enrollment code. Needed for the first install (or to enroll again); not for updates.

.PARAMETER DryRun
  Test mode: the service only logs what it would close and does not shut the PC down.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -ServerUrl https://monitor-device.me.workers.dev -Code ABCD2345EF
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1          # update the program, keep the enrollment
#>
#Requires -RunAsAdministrator
param(
    [string] $ServerUrl,
    [string] $Code,
    [switch] $DryRun,
    [string] $Source = (Join-Path $PSScriptRoot 'app')
)
$ErrorActionPreference = 'Stop'

$ServiceName = 'MonitorV2'
$InstallDir = Join-Path $env:ProgramFiles 'MonitorV2'
$DataDir = Join-Path $env:ProgramData 'MonitorV2'
$Exe = Join-Path $InstallDir 'Monitor.Service.exe'

if (-not (Test-Path (Join-Path $Source 'Monitor.Service.exe'))) {
    throw "Monitor.Service.exe not found in $Source. Build it first with v2\client\publish.ps1."
}
if (-not $Code -and -not (Test-Path (Join-Path $DataDir 'device.json'))) {
    throw 'This PC is not enrolled yet: pass -ServerUrl and -Code (web app -> PCs -> Add PC).'
}
if ($Code -and -not $ServerUrl) { throw '-ServerUrl is required with -Code.' }

$old = Get-Process -Name Monitor -ErrorAction SilentlyContinue
if ($old) {
    Write-Warning 'The original Monitor app is running. Both close programs and set Edge policies: uninstall it before relying on v2.'
}

# 1. Stop the running version.
$service = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host 'Stopping the service...'
    Stop-Service $ServiceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
Get-Process -Name Monitor.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. Copy the program. Program Files keeps its default permissions: users can read and run, not change.
Write-Host "Copying to $InstallDir..."
New-Item -ItemType Directory -Force $InstallDir | Out-Null
robocopy $Source $InstallDir /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the files failed (robocopy exit code $LASTEXITCODE)." }
Get-ChildItem $InstallDir -Recurse -File | Unblock-File

# 3. Enroll.
if ($Code) {
    & $Exe enroll $ServerUrl $Code
    if ($LASTEXITCODE -ne 0) { throw 'Enrollment failed (see the message above). Codes expire after 30 minutes.' }
}

# 4. Register the service (LocalSystem, automatic start) and restart it if it ever stops.
$imagePath = '"' + $Exe + '"'
if ($DryRun) { $imagePath += ' --dry-run' }
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName $imagePath -DisplayName 'Monitor v2' -StartupType Automatic `
        -Description 'Screen and game time monitor. Keeps School mode enforced and talks to the Monitor server.' | Out-Null
}
else {
    Set-Service -Name $ServiceName -StartupType Automatic
}
# Set the command line directly (sc.exe mangles embedded quotes when called from PowerShell).
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value $imagePath
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ObjectName -Value 'LocalSystem'
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

Write-Host 'Starting the service...'
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

& $Exe status
Write-Host ''
Write-Host "Installed$(if ($DryRun) { ' in TEST MODE (nothing is closed, no shutdown)' })."
Write-Host "Logs: $DataDir\logs. The tray app starts within a few seconds in each standard user's session."

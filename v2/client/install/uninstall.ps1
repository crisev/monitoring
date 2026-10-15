<#
.SYNOPSIS
  Removes Monitor v2 from this PC: stops and deletes the service, removes the Edge policies and the program.
  Run in an administrator PowerShell.

.PARAMETER KeepData
  Keep C:\ProgramData\MonitorV2 (the enrollment and logs), e.g. to reinstall without a new code.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
#Requires -RunAsAdministrator
param([switch] $KeepData)
$ErrorActionPreference = 'Stop'

$ServiceName = 'MonitorV2'
$InstallDir = Join-Path $env:ProgramFiles 'MonitorV2'
$DataDir = Join-Path $env:ProgramData 'MonitorV2'
$Exe = Join-Path $InstallDir 'Monitor.Service.exe'

$service = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host 'Stopping the service...'
        Stop-Service $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host 'Service removed.'
}
Get-Process -Name Monitor.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $Exe) {
    & $Exe clear-policies
}
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir."
}
if (-not $KeepData -and (Test-Path $DataDir)) {
    Remove-Item $DataDir -Recurse -Force
    Write-Host "Removed $DataDir."
}
Write-Host 'Monitor v2 is uninstalled.'

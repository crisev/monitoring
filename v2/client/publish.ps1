<#
.SYNOPSIS
  Builds the Windows client into dist\MonitorV2: app\ (service + agent, self-contained, no .NET install needed)
  plus install.ps1 and uninstall.ps1. Copy that folder to the PC and run install.ps1 there.

.EXAMPLE
  .\publish.ps1
  .\publish.ps1 -Version 0.2.0 -Zip
#>
param(
    [string] $Version,
    [switch] $Zip
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'dist\MonitorV2'
$app = Join-Path $out 'app'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $app, '-p:DebugType=none', '-p:GenerateDocumentationFile=false')
if ($Version) { $common += "-p:Version=$Version" }

# Both programs go to the same folder and share the runtime files.
foreach ($project in 'Monitor.Service', 'Monitor.Agent') {
    dotnet publish $project @common
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}
Copy-Item (Join-Path $PSScriptRoot 'install\*.ps1') $out

$size = (Get-ChildItem $app -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published to {0} ({1:N0} MB)." -f $out, $size)

if ($Zip) {
    $zipPath = Join-Path $PSScriptRoot 'dist\MonitorV2.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path $out -DestinationPath $zipPath
    Write-Host "Zipped to $zipPath."
}

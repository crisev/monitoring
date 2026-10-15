<#
.SYNOPSIS
  Installs a downloaded update and rolls back if it doesn't work. Started by the Monitor v2 service
  (as SYSTEM), never by hand.

.DESCRIPTION
  The service runs the copy of this script from the version that is installed *now*, so a broken new
  version can't break its own rollback.
    1. stop the service and the agents
    2. back up the install folder
    3. copy the new files (enrollment and settings in ProgramData are not touched)
    4. start the service and wait until the new version writes update-ok.txt (it does that after its
       first successful server call)
    5. if that doesn't happen within -TimeoutSeconds, put the backup back and start the old version
  The result goes to update-result.json; the version that runs afterwards reports it to the server.
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Version,
    [string] $From = '?',
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'MonitorV2'),
    [string] $DataDir = (Join-Path $env:ProgramData 'MonitorV2'),
    [int] $TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'

$ServiceName = 'MonitorV2'
$Backup = Join-Path $DataDir 'updates\backup'
$OkFile = Join-Path $DataDir 'update-ok.txt'
$ResultFile = Join-Path $DataDir 'update-result.json'
$LogDir = Join-Path $DataDir 'logs'
New-Item -ItemType Directory -Force $LogDir | Out-Null
$LogFile = Join-Path $LogDir ("update-{0:yyyyMMdd}.log" -f (Get-Date))

function Write-Log([string] $Message) {
    "{0:yyyy-MM-dd HH:mm:ss.fff} [update] {1}" -f (Get-Date), $Message | Add-Content -Path $LogFile -Encoding utf8
}

function Write-Result([bool] $Ok, [string] $Message) {
    @{ to = $Version; ok = $Ok; message = $Message } | ConvertTo-Json | Set-Content -Path $ResultFile -Encoding utf8
}

function Stop-Monitor {
    $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    Get-Process -Name Monitor.Agent, Monitor.Service -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2 # let Windows release the files
}

function Copy-Mirror([string] $From, [string] $To) {
    robocopy $From $To /MIR /R:10 /W:3 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying $From to $To failed (robocopy exit code $LASTEXITCODE)" }
}

function Wait-NewVersion {
    # A stopwatch, not the wall clock: nothing here depends on the PC's time.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ((Test-Path $OkFile) -and ((Get-Content $OkFile -Raw).Trim() -eq $Version)) { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

$step = 'start'
try {
    Write-Log "Updating $From -> $Version from $Source"
    if (-not (Test-Path (Join-Path $Source 'Monitor.Service.exe'))) { throw "no Monitor.Service.exe in $Source" }
    Remove-Item $OkFile, $ResultFile -ErrorAction SilentlyContinue

    $step = 'stop'
    Stop-Monitor

    $step = 'backup'
    Copy-Mirror $InstallDir $Backup
    Write-Log "Backed up $InstallDir"

    $step = 'copy'
    Copy-Mirror $Source $InstallDir
    Write-Log 'New files copied; starting the service'

    $step = 'wait'
    Start-Service $ServiceName
    if (Wait-NewVersion) {
        Write-Log "Version $Version reached the server: done"
        Write-Result $true 'ok'
        Remove-Item $OkFile -ErrorAction SilentlyContinue
        exit 0
    }
    throw "the new version did not reach the server within $TimeoutSeconds seconds"
}
catch {
    $reason = "$_"
    Write-Log "Failed at step '$step': $reason"
    if ($step -in 'copy', 'wait') {
        try {
            Write-Log 'Rolling back'
            Stop-Monitor
            Copy-Mirror $Backup $InstallDir
            $reason = "$reason (rolled back to $From)"
        }
        catch {
            Write-Log "Rollback failed: $_"
            $reason = "$reason; rollback failed: $_"
        }
    }
    Write-Result $false $reason
    try { Start-Service $ServiceName } catch { Write-Log "Could not start the service: $_" }
    exit 1
}

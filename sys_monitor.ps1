# Fortam utilizarea protocolului TLS 1.2 pentru a putea comunica cu serverele Discord
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Hide the console window
Add-Type -Name Window -Namespace Console -MemberDefinition '
[DllImport("Kernel32.dll")]
public static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
public static extern bool ShowWindow(IntPtr hWnd, Int32 nCmdShow);
'
$consolePtr = [Console.Window]::GetConsoleWindow()
[Console.Window]::ShowWindow($consolePtr, 0)  # 0 = SW_HIDE

# Definim functiile C# necesare pentru a citi fereastra activa din Windows
$code = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WindowAPI {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
"@

# Check if WindowAPI type already exists
if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType("WindowAPI", $false, $false) } | Where-Object { $_ })) {
    Add-Type -TypeDefinition $code
}

$webhookUrl = "https://discord.com/api/webhooks/1500559708673544323/P7RBYmQ7RBaOiVGf7LV390gpr5F3OIqjjHPcOLOEp1APjfrd0NurhYq9DDpLqIaQqK2B"

$googleWebhookUrl = "https://script.google.com/macros/s/AKfycbynf7m-zQPvDTrLPp6SlqLE86BY43iClfRq0CjGvvg-OoYMPOn_ty1PCDfnUMJDFzlONQ/exec"

# GitHub gist URL containing the block lists (JSON format)
$BlockListGistUrl = "https://gist.githubusercontent.com/crisev/e9e46b188aaf1651daea86c95f363992/raw/gistfile1.txt"

# Default block lists (used if gist cannot be fetched)
$blockedProcessNames = @("duckduckgo")
$blockedPageTitles = @("YouTube")

# Function to fetch block lists from gist
function Get-BlockListsFromGist {
    param([string]$GistUrl)
    
    if (-not $GistUrl) { return $null }
    
    try {
        $response = Invoke-RestMethod -Uri $GistUrl -Method Get -ErrorAction Stop
        if ($response -is [string]) { $response = $response | ConvertFrom-Json }
        return $response
    } catch {
        Write-Host "Failed to fetch block lists from gist: $($_.Exception.Message)"
        return $null
    }
}

# Get current user
$currentUser = $env:USERNAME

# Constants
$scanIntervalSeconds = 5
$reportIntervalSeconds = 360  # 5 minutes
$loopsNeeded = [math]::Ceiling($reportIntervalSeconds / $scanIntervalSeconds)

# Dictionar in care tinem timpul pentru fiecare aplicatie
$appStats = @{}
$loops = 0

# Bucla infinita
while ($true) {
    # Get foreground window
    $hwnd = [WindowAPI]::GetForegroundWindow()
    if ($hwnd -ne [IntPtr]::Zero) {
        $processId = 0
        [WindowAPI]::GetWindowThreadProcessId($hwnd, [ref]$processId) | Out-Null
        if ($processId -gt 0) {
            $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($process) {
                $name = $process.ProcessName
                # Get window title
                $windowTitle = ""
                $sb = New-Object System.Text.StringBuilder(256)
                [WindowAPI]::GetWindowText($hwnd, $sb, 256) | Out-Null
                $windowTitle = $sb.ToString()
                
                # Create display key for reporting
                $displayKey = $name
                if ($name -eq "chrome" -and $windowTitle) {
                    $pageName = $windowTitle -replace " - Google Chrome.*$", ""
                    $displayKey = "$name - $pageName"
                } elseif ($name -eq "firefox" -and $windowTitle) {
                    $pageName = $windowTitle -replace " - Mozilla Firefox.*$", ""
                    $displayKey = "$name - $pageName"
                } elseif ($name -eq "msedge" -and $windowTitle) {
                    $pageName = $windowTitle -replace " - Microsoft Edge.*$", ""
                    $displayKey = "$name - $pageName"
                }
                
                Write-Host "Detected foreground: $displayKey"
                
                # Check if should be blocked
                $isBlocked = $false
                
                # Check page title against block list
                foreach ($keyword in $blockedPageTitles) {
                    if ($windowTitle.IndexOf($keyword, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        Write-Host "BLOCKED: '$windowTitle' contains '$keyword' - killing process"
                        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
                        $isBlocked = $true
                        break
                    }
                }
                
                # Check process name against block list
                if (-not $isBlocked -and $blockedProcessNames -contains $name) {
                    Write-Host "BLOCKED: Process '$name' is forbidden - killing process"
                    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
                    $isBlocked = $true
                }
                
                # Track time for reporting (if not blocked or system process)
                if (-not $isBlocked -and $name -notin @("Idle", "LockApp", "SearchUI")) {
                    if (-not $appStats.ContainsKey($displayKey)) { $appStats[$displayKey] = 0 }
                    $appStats[$displayKey] += $scanIntervalSeconds
                }
            }
        }
    }

    Start-Sleep -Seconds $scanIntervalSeconds
    $loops++

    # Every report interval: fetch fresh block lists and send report
    if ($loops -ge $loopsNeeded) {
        # Refresh block lists from gist
        $gistData = Get-BlockListsFromGist -GistUrl $BlockListGistUrl
        if ($gistData) {
            if ($gistData.blockedProcessNames) {
                $blockedProcessNames = $gistData.blockedProcessNames
            }
            if ($gistData.blockedPageTitles) {
                $blockedPageTitles = $gistData.blockedPageTitles
            }
            # Write-Host "=== Gist update OK ==="
            # Write-Host "  blockedProcessNames: $($blockedProcessNames -join ', ')"
            # Write-Host "  blockedPageTitles:   $($blockedPageTitles -join ', ')"
        } else {
            # Write-Host "=== Gist update FAILED (null response - check JSON syntax) ==="
        }
        
        # Send report
        $message = "**Raport Activitate**`n"
        $message += "**Utilizator:** $currentUser`n"
        $message += "**Ora:** " + (Get-Date).ToString("HH:mm") + "`n"
        $message += "`n"
        
        # Sortam aplicatiile dupa timpul petrecut (descrescator)
        $sortedStats = $appStats.GetEnumerator() | Sort-Object Value -Descending
        
        # Prepare data for Google Sheets
        $data = @()
        $activityFound = $false
        
        foreach ($stat in $sortedStats) {
            $seconds = $stat.Value
            if ($seconds -ge 6) {
                $activityFound = $true
                $message += "- **$($stat.Name)**: $seconds secunde`n"
                $data += @([PSCustomObject]@{ appName = $stat.Name; seconds = $seconds; user = $currentUser })
            }
        }

        if (-not $activityFound) {
            $message += "- nici o activitate detectată în ultimele $reportIntervalSeconds secunde`n"
        }

        # Send data to Google Sheets when activity exists
        if ($data.Count -gt 0) {
            $jsonData = $data | ConvertTo-Json
            # Write-Host "=== Sending to Google Sheets ==="
            # Write-Host $jsonData
            Invoke-RestMethod -Uri $googleWebhookUrl -Method Post -Body $jsonData -ContentType 'application/json' -ErrorAction SilentlyContinue
            # Write-Host "=== Google Sheets request sent ==="
        }

        # Send to Discord always
        $payload = @{ content = $message } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri $webhookUrl -Method Post -Body $payload -ContentType 'application/json' -ErrorAction SilentlyContinue

        # Reset for next period
        $appStats.Clear()
        $loops = 0
    }
}
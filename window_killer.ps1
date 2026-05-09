# window_killer.ps1
# This script enumerates top-level visible windows and closes any whose title matches a blocked keyword.
# Block lists are fetched from a GitHub gist for easy updates.

# GitHub gist URL containing the block lists (JSON format)
$BlockListGistUrl = "https://gist.githubusercontent.com/crisev/e9e46b188aaf1651daea86c95f363992/raw/c29742d6635d79eff8168b35ce86a06fbbfa19a5/gistfile1.txt"  # Set this to your gist raw URL

# Default block lists (used if gist cannot be fetched)
$global:BlockedProcessNames = @(
    "duckduckgo"
)

$global:BlockedPageTitles = @(
    "YouTube"
)

function Get-BlockListsFromGist {
    param(
        [string]$GistUrl
    )
    
    if (-not $GistUrl) {
        return $null
    }
    
    try {
        $response = Invoke-RestMethod -Uri $GistUrl -Method Get -ErrorAction Stop
        if ($response -is [string]) {
            $response = $response | ConvertFrom-Json
        }
        return $response
    } catch {
        Write-Host "Failed to fetch block lists from gist: $($_.Exception.Message)"
        return $null
    }
}

# Try to fetch block lists from gist
if ($BlockListGistUrl) {
    $gistData = Get-BlockListsFromGist -GistUrl $BlockListGistUrl
    if ($gistData) {
        if ($gistData.blockedProcessNames) {
            $global:BlockedProcessNames = $gistData.blockedProcessNames
        }
        if ($gistData.blockedPageTitles) {
            $global:BlockedPageTitles = $gistData.blockedPageTitles
        }
    }
}

if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType("WindowKillerNative", $false, $false) } | Where-Object { $_ })) {
    $killerApi = @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class WindowKillerNative {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
"@

    Add-Type -TypeDefinition $killerApi
}

function Get-OpenWindows {
    $windows = @()

    $callback = [WindowKillerNative+EnumWindowsProc] {
        param($hWnd, $lParam)

        if (-not [WindowKillerNative]::IsWindowVisible($hWnd)) {
            return $true
        }

        $sb = New-Object System.Text.StringBuilder 512
        [WindowKillerNative]::GetWindowText($hWnd, $sb, $sb.Capacity) | Out-Null
        $title = $sb.ToString().Trim()
        if ([string]::IsNullOrWhiteSpace($title)) {
            return $true
        }

        $processId = 0
        [WindowKillerNative]::GetWindowThreadProcessId($hWnd, [ref]$processId) | Out-Null

        $windows += [PSCustomObject]@{
            Handle = $hWnd
            Title = $title
            ProcessId = $processId
        }

        return $true
    }

    [WindowKillerNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $windows
}

function Invoke-WindowKiller {
    $openWindows = Get-OpenWindows
    
    # Also check the foreground window specifically (important for browsers with tabs)
    $foregroundHwnd = [WindowKillerNative]::GetForegroundWindow()
    if ($foregroundHwnd -ne [IntPtr]::Zero) {
        $fgProcessId = 0
        [WindowKillerNative]::GetWindowThreadProcessId($foregroundHwnd, [ref]$fgProcessId) | Out-Null
        if ($fgProcessId -gt 0) {
            $fgProcess = Get-Process -Id $fgProcessId -ErrorAction SilentlyContinue
            if ($fgProcess) {
                $sb = New-Object System.Text.StringBuilder 512
                [WindowKillerNative]::GetWindowText($foregroundHwnd, $sb, $sb.Capacity) | Out-Null
                $fgTitle = $sb.ToString().Trim()
                
                # Add foreground window to the list if not already there
                $fgWindowExists = $openWindows | Where-Object { $_.Handle -eq $foregroundHwnd }
                if (-not $fgWindowExists) {
                    $openWindows += [PSCustomObject]@{
                        Handle = $foregroundHwnd
                        Title = $fgTitle
                        ProcessId = $fgProcessId
                    }
                }
            }
        }
    }
    
    # Debug: Show Chrome-related windows
    $chromeWindows = $openWindows | Where-Object { 
        $proc = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        $proc -and $proc.ProcessName -eq "chrome"
    }
    if ($chromeWindows) {
        Write-Host "Found $($chromeWindows.Count) Chrome windows:"
        foreach ($win in $chromeWindows) {
            Write-Host "  Chrome window: '$($win.Title)'"
        }
    } else {
        Write-Host "No Chrome windows found by window killer"
    }

    foreach ($win in $openWindows) {
        $process = Get-Process -Id $win.ProcessId -ErrorAction SilentlyContinue
        
        $titleMatches = $false
        
        # Check if page title matches any blocked keyword
        foreach ($keyword in $global:BlockedPageTitles) {
            if ($win.Title.IndexOf($keyword, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Write-Host "🚫 BLOCKED: '$($win.Title)' contains '$keyword' - closing window"
                $titleMatches = $true
                break
            }
        }

        # Check if process name matches if title didn't match
        if (-not $titleMatches -and $global:BlockedProcessNames -and $process) {
            if ($global:BlockedProcessNames -contains $process.ProcessName) {
                Write-Host "🚫 BLOCKED: Process '$($process.ProcessName)' is forbidden - terminating"
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $titleMatches = $true
            }
        }

        # Special handling for browsers - kill the process if blocked content is detected
        if (-not $titleMatches -and $process) {
            # Check for common browser process names (expandable list)
            $browserProcesses = @("chrome", "firefox", "msedge", "opera", "brave", "vivaldi", "duckduckgo", "browser", "iexplore", "safari")
            $isBrowser = $browserProcesses -contains $process.ProcessName -or $process.ProcessName -like "*browser*" -or $process.ProcessName -like "*chrome*" -or $process.ProcessName -like "*edge*"
            
            if ($isBrowser) {
                # For browsers, check if we should kill the entire process
                # Check all windows of this browser process for blocked content
                $browserWindows = $openWindows | Where-Object { 
                    $browserProc = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                    $browserProc -and ($browserProcesses -contains $browserProc.ProcessName -or $browserProc.ProcessName -like "*browser*" -or $browserProc.ProcessName -like "*chrome*" -or $browserProc.ProcessName -like "*edge*")
                }
                
                $hasBlockedContent = $false
                foreach ($browserWin in $browserWindows) {
                    foreach ($keyword in $global:BlockedPageTitles) {
                        if ($browserWin.Title.IndexOf($keyword, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            Write-Host "🚫 BLOCKED: $($process.ProcessName) accessing '$keyword' - terminating browser"
                            $hasBlockedContent = $true
                            break
                        }
                    }
                    if ($hasBlockedContent) { break }
                }
                
                if ($hasBlockedContent) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    $titleMatches = $true
                }
            }
        }

        if ($titleMatches) {
            # Close the window if it was a direct match (not a browser process termination)
            if ($process -and -not ($browserProcesses -contains $process.ProcessName -or $process.ProcessName -like "*browser*" -or $process.ProcessName -like "*chrome*" -or $process.ProcessName -like "*edge*")) {
                [WindowKillerNative]::SendMessage($win.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            }
        }
    }
}

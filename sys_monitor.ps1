# Fortam utilizarea protocolului TLS 1.2 pentru a putea comunica cu serverele Discord
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Definim functiile C# necesare pentru a citi fereastra activa din Windows
$code = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32 {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
"@
Add-Type -TypeDefinition $code

$webhookUrl = "https://discord.com/api/webhooks/1500559708673544323/P7RBYmQ7RBaOiVGf7LV390gpr5F3OIqjjHPcOLOEp1APjfrd0NurhYq9DDpLqIaQqK2B"

$googleWebhookUrl = "https://script.google.com/macros/s/AKfycbynf7m-zQPvDTrLPp6SlqLE86BY43iClfRq0CjGvvg-OoYMPOn_ty1PCDfnUMJDFzlONQ/exec"

# Get current user
$currentUser = $env:USERNAME

# Constants
$scanIntervalSeconds = 5
$reportIntervalSeconds = 300  # 5 minutes
$loopsNeeded = [math]::Ceiling($reportIntervalSeconds / $scanIntervalSeconds)

# Dictionar in care tinem timpul pentru fiecare aplicatie
$appStats = @{}
$loops = 0

# Bucla infinita
while ($true) {
    # 1. Aflam fereastra activa
    $hwnd = [Win32]::GetForegroundWindow()
    if ($hwnd -ne [IntPtr]::Zero) {
        $processId = 0
        [Win32]::GetWindowThreadProcessId($hwnd, [ref]$processId) | Out-Null
        if ($processId -gt 0) {
            $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($process) {
                $name = $process.ProcessName
                
                # Get window title for browsers and other apps
                $windowTitle = ""
                $sb = New-Object System.Text.StringBuilder(256)
                [Win32]::GetWindowText($hwnd, $sb, 256) | Out-Null
                $windowTitle = $sb.ToString()
                
                # Create a display key combining process name and window title for browsers
                $displayKey = $name
                if ($name -eq "chrome" -and $windowTitle) {
                    # Extract just the page name (usually before the first " - " or at the end)
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
                
                # Ignoram procesele de sistem irelevante (ecran de blocare, etc.)
                if ($name -notin @("Idle", "LockApp", "SearchUI")) {
                    if (-not $appStats.ContainsKey($displayKey)) { $appStats[$displayKey] = 0 }
                    # Adaugam timpul scanat la aplicatia respectiva
                    $appStats[$displayKey] += $scanIntervalSeconds
                }
            }
        }
    }

    # 2. Asteptam 10 secunde
    Start-Sleep -Seconds $scanIntervalSeconds
    $loops++

    # 3. Dupa intervalul setat trimitem datele
    if ($loops -ge $loopsNeeded) {
        if ($appStats.Count -gt 0) {
            $message = "**Raport Activitate**`n"
            $message += "**Utilizator:** $currentUser`n"
            $message += "**Ora:** " + (Get-Date).ToString("HH:mm") + "`n"
            $message += "`n"
            
            # Sortam aplicatiile dupa timpul petrecut (descrescator)
            $sortedStats = $appStats.GetEnumerator() | Sort-Object Value -Descending
            
            # Prepare data for Google Sheets
            $data = @()
            
            foreach ($stat in $sortedStats) {
                # Timpul in secunde
                $seconds = $stat.Value
                if ($seconds -ge 6) { # Afisam doar aplicatiile folosite mai mult de 6 secunde
                    $message += "- **$($stat.Name)**: $seconds secunde`n"
                    $data += @{appName = $stat.Name; seconds = $seconds; user = $currentUser}
                }
            }

            # Send data to Google Sheets
            if ($data.Count -gt 0) {
                $jsonData = @($data) | ConvertTo-Json
                Invoke-RestMethod -Uri $googleWebhookUrl -Method Post -Body $jsonData -ContentType 'application/json' -ErrorAction SilentlyContinue
                Write-Host "Data sent to Google Sheets: $($data.Count) entries"
            }

            # Manually create JSON payload for Discord to avoid escaping issues
            $escapedMessage = $message -replace '\\', '\\\\' -replace '"', '\"' -replace "`n", '\n' -replace "`r", '\r' -replace "`t", '\t'
            $payload = '{"content": "' + $escapedMessage + '"}'

            # Trimitem catre webhook
            Invoke-RestMethod -Uri $webhookUrl -Method Post -Body $payload -ContentType 'application/json' -ErrorAction SilentlyContinue
        }

        # Resetam datele pentru urmatoarea ora
        $appStats.Clear()
        $loops = 0
    }
}
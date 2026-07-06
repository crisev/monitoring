/*
 * ======================================================================================
 * Windows Activity Monitor & Parental Control Utility
 * ======================================================================================
 * 
 * --- CONTEXT & PURPOSE ---
 * Designed as a parental control tool to monitor educational computer usage, 
 * enforce application/website restrictions, and capture screenshots to identify workarounds.
 * 
 * --- WHAT IT DOES ---
 * - Monitors active foreground applications, tracking the active process name and window title.
 * - Tracks background and foreground audio playback activity using CoreAudio APIs.
 * - Enforces process restrictions by checking running processes against a blocklist
 *   (matching by process name or window title keywords) and terminating any matches.
 * - Aggregates time spent on active foreground applications and applications playing audio
 *   (recorded in seconds).
 * 
 * --- HOW IT COMMUNICATES ---
 * - Inbound: Periodically fetches a dynamic blocklist from a raw GitHub Gist URL in JSON format.
 * - Outbound: Periodically reports aggregated activity statistics to:
 *   1. A Google Sheets Apps Script Webhook (POST JSON containing seconds per application).
 *   2. A Discord Channel Webhook (POST JSON containing a formatted markdown activity summary).
 * 
 * --- HOW IT IS CONFIGURED ---
 * - Webhooks and Source URLs: Hardcoded fields in the Program class:
 *     * TextWebhookUrl: Discord webhook link for text reports.
 *     * ImageWebhookUrl: Discord webhook link for screenshot reports.
 *     * GoogleWebhookUrl: Google Web App macro link.
 *     * BlockListGistUrl: Raw Github Gist URL for process/title blocklists.
 * - Scan and Report Intervals:
 *     * ScanIntervalSeconds: Time between active checks/scans (default: 5 seconds).
 *     * ReportIntervalSeconds: Time between reporting events (default: 360 seconds / 6 minutes).
 * - Startup Visibility:
 *     * By default, the application uses Win32 API (ShowWindow with SW_HIDE) to hide the console window.
 *     * Passing the "--visible" command line argument keeps the console window shown.
 *     * To prevent a console window from opening initially at all, the project file (Monitor.csproj)
 *       can be configured with <OutputType>WinExe</OutputType> instead of <OutputType>Exe</OutputType>.
 *     * Alternatively, it can be launched via a script such as the included run_hidden.vbs.

 dotnet run -- --visible
 dotnet publish -c Release 
 * ======================================================================================
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Monitor
{
    // COM Interop definitions for CoreAudio API
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator {}

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator {
        int NotImpl1();
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2 {
        int NotImpl1();
        int NotImpl2();
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator {
        [PreserveSig]
        int GetCount(out int SessionCount);
        [PreserveSig]
        int GetSession(int SessionCount, out IAudioSessionControl2 Session);
    }

    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2 {
        int NotImpl0();
        int NotImpl1();
        int NotImpl2();
        int NotImpl3();
        int NotImpl4();
        int NotImpl5();
        int NotImpl6();
        int NotImpl7();
        int NotImpl8();
        int NotImpl9();
        int NotImpl10();
        [PreserveSig]
        int GetProcessId(out uint pRetVal);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation {
        [PreserveSig]
        int GetPeakValue(out float pfPeak);
    }

    class Program
    {
        // Win32 API definitions
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeleteFile(string lpFileName);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        const int SW_HIDE = 0;

        // Configuration
        private const string TextWebhookUrl = "https://discord.com/api/webhooks/1500559708673544323/P7RBYmQ7RBaOiVGf7LV390gpr5F3OIqjjHPcOLOEp1APjfrd0NurhYq9DDpLqIaQqK2B";
        private const string ImageWebhookUrl = "https://discord.com/api/webhooks/1521898764447518802/Y8CtiAzRUJIroO3rnzyVSRClDLIdQsOEVV2HTMgR_d7b9DxqOwfYGOiov7R-Ujeu6UYR";
        private const string GoogleWebhookUrl = "https://script.google.com/macros/s/AKfycbynf7m-zQPvDTrLPp6SlqLE86BY43iClfRq0CjGvvg-OoYMPOn_ty1PCDfnUMJDFzlONQ/exec";
        private const string BlockListGistUrl = "https://gist.githubusercontent.com/crisev/e9e46b188aaf1651daea86c95f363992/raw/gistfile1.txt";
        private static string updateUrl = "";

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetKernelObjectSecurity(IntPtr Handle, int securityInformation, [Out] byte[] pSecurityDescriptor, uint nLength, out uint lpnLengthNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool SetKernelObjectSecurity(IntPtr Handle, int securityInformation, [In] byte[] pSecurityDescriptor);

        private static List<string> blockedProcessNames = new List<string> 
        { 
            "duckduckgo",
            "opera",
            "GettingOverIt",
            "FPSChess-Win64-Shipping",
            "cs2",
            "steamwebhelper",
            "Discord",
            "Grapples Galore",
            "AimLab_tb",
            "FortniteClient-Win64-Shipping",
            "chrome",
            "GeometryDash",
            "RobloxPlayerBeta" 
        };
        private static List<string> blockedPageTitles = new List<string> {     
            "YouTube",
            "Agar.io",
            "diep.io",
            "EvoWorld",
            "mope.io",
            "Lordz.io",
            "Twitch",
            "EVOWORLD",
            "Game",
            "CRYZEN",
            "Poki",
            "Infinite Craft",
            "ZOMBS.io"
        };

        private static Dictionary<string, int> appStats = new Dictionary<string, int>();
        private static Dictionary<string, int> audioStats = new Dictionary<string, int>();

        private static DateTime lastScreenshotTime = DateTime.MinValue;

        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly HttpClient redirectHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        private static readonly string currentUser = Environment.UserName;

        private static int scanIntervalSeconds = 5;
        private static int reportIntervalSeconds = 360;
        private static int screenshotIntervalSeconds = 60;

        private static Mutex singleInstanceMutex = new Mutex(true, "{8F6F0AC4-B9A1-45fd-A8CF-72F04E6BDE8F}");

        private static List<TimeInterval> configuredIntervals = new List<TimeInterval>();
        private static OverlayForm overlayForm;
        private static Thread overlayThread;
        private static bool wasInInterval = false;

        private static void StartOverlayThread()
        {
            overlayThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                overlayForm = new OverlayForm();
                var handle = overlayForm.Handle; // Force handle creation
                Application.Run();
            });
            overlayThread.SetApartmentState(ApartmentState.STA);
            overlayThread.IsBackground = true;
            overlayThread.Start();
        }

        static async Task Main(string[] args)
        {
            if (args.Contains("--test-overlay", StringComparer.OrdinalIgnoreCase))
            {
                StartOverlayThread();
                while (overlayForm == null || !overlayForm.IsHandleCreated) Thread.Sleep(100);
                overlayForm.UpdateCountdown(TimeSpan.FromMinutes(9).Add(TimeSpan.FromSeconds(59)));
                MessageBox.Show("Overlay is currently visible. Click OK to close.", "Test Overlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!singleInstanceMutex.WaitOne(TimeSpan.Zero, true))
            {
                // Another instance is already running. Exit silently.
                return;
            }

            // Protect the process from being terminated by the current user (requires Admin to kill)
            ProtectProcess();

            // Clean up backup file if it exists from a previous update
            try
            {
                string currentExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(currentExe))
                {
                    string backupExe = currentExe + ".bak";
                    if (File.Exists(backupExe))
                    {
                        File.Delete(backupExe);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clean up backup file: {ex.Message}");
            }

            // Hide or allocate console window depending on --visible
            if (args.Contains("--visible", StringComparer.OrdinalIgnoreCase))
            {
                AllocConsole();
            }
            else
            {
                IntPtr consolePtr = GetConsoleWindow();
                if (consolePtr != IntPtr.Zero)
                {
                    ShowWindow(consolePtr, SW_HIDE);
                }
            }

            int loops = 0;

            LoadIntervalsFromRegistry();

            TimeSpan currentTime = DateTime.Now.TimeOfDay;
            var activeInterval = configuredIntervals.FirstOrDefault(i => i.IsActive(currentTime));

            if (activeInterval == null && configuredIntervals.Count > 0)
            {
                Console.WriteLine("Started outside defined intervals. Forcing update from gist...");
                await RefreshBlockListsAsync();
                
                currentTime = DateTime.Now.TimeOfDay;
                activeInterval = configuredIntervals.FirstOrDefault(i => i.IsActive(currentTime));
                
                if (activeInterval == null)
                {
                    Console.WriteLine("No active interval after update. Shutting down in 5 seconds...");
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 5") { CreateNoWindow = true, UseShellExecute = false });
                    return;
                }
            }
            else
            {
                await RefreshBlockListsAsync();
            }
            
            wasInInterval = activeInterval != null;
            StartOverlayThread();

            while (true)
            {
                try
                {
                    TimeSpan now = DateTime.Now.TimeOfDay;
                    var currentActiveInterval = configuredIntervals.FirstOrDefault(i => i.IsActive(now));
                    bool isInInterval = currentActiveInterval != null;

                    if (wasInInterval && !isInInterval)
                    {
                        Console.WriteLine("Interval finished and no other interval is active. Shutting down.");
                        Process.Start(new ProcessStartInfo("shutdown", "/s /t 5") { CreateNoWindow = true, UseShellExecute = false });
                        Environment.Exit(0);
                    }
                    wasInInterval = isInInterval;

                    if (isInInterval)
                    {
                        TimeSpan end = currentActiveInterval.End;
                        TimeSpan remaining;
                        if (end >= now)
                        {
                            remaining = end - now;
                        }
                        else
                        {
                            remaining = TimeSpan.FromHours(24) - now + end;
                        }

                        if (remaining.TotalMinutes <= 10)
                        {
                            if (overlayForm != null && overlayForm.IsHandleCreated)
                            {
                                overlayForm.UpdateCountdown(remaining);
                            }
                        }
                        else
                        {
                            if (overlayForm != null && overlayForm.IsHandleCreated)
                            {
                                overlayForm.HideOverlay();
                            }
                        }
                    }
                    else
                    {
                        if (overlayForm != null && overlayForm.IsHandleCreated)
                        {
                            overlayForm.HideOverlay();
                        }
                    }

                    bool isGaming = currentActiveInterval != null && currentActiveInterval.Type.Equals("Gaming", StringComparison.OrdinalIgnoreCase);

                    // 1. Get all running processes
                    var allProcesses = Process.GetProcesses();

                    // 2. Global process blocklist check
                    if (!isGaming)
                    {
                        foreach (var proc in allProcesses)
                    {
                        try
                        {
                            string procName = proc.ProcessName;
                            string mainTitle = "";
                            try
                            {
                                mainTitle = proc.MainWindowTitle;
                            }
                            catch { /* Ignore access denied on system processes */ }

                            bool isBlocked = false;

                            // Check process name
                            if (blockedProcessNames.Contains(procName, StringComparer.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"BLOCKED: Process '{procName}' is forbidden - killing.");
                                proc.Kill(true);
                                isBlocked = true;
                            }

                            // Check window title
                            if (!isBlocked && !string.IsNullOrEmpty(mainTitle))
                            {
                                foreach (var keyword in blockedPageTitles)
                                {
                                    if (mainTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                                    {
                                        Console.WriteLine($"BLOCKED: Title '{mainTitle}' contains keyword '{keyword}' - killing process '{procName}'.");
                                        proc.Kill(true);
                                        break;
                                    }
                                }
                            }
                        }
                        catch { /* Ignore errors for individual processes */ }
                    }
                }

                    // Refresh processes list (since some might have been killed)
                    allProcesses = Process.GetProcesses();

                    // 3. Foreground Application Tracking
                    IntPtr fgHwnd = GetForegroundWindow();
                    if (fgHwnd != IntPtr.Zero)
                    {
                        uint pid = 0;
                        GetWindowThreadProcessId(fgHwnd, out pid);
                        if (pid > 0)
                        {
                            var proc = allProcesses.FirstOrDefault(p => p.Id == pid);
                            if (proc != null)
                            {
                                string name = proc.ProcessName;
                                StringBuilder sb = new StringBuilder(256);
                                GetWindowText(fgHwnd, sb, 256);
                                string windowTitle = sb.ToString();

                                string displayKey = GetDisplayKey(name, windowTitle);

                                string[] ignoredApps = { "Idle", "LockApp", "SearchUI" };
                                if (!ignoredApps.Contains(name, StringComparer.OrdinalIgnoreCase))
                                {
                                    if (!appStats.ContainsKey(displayKey))
                                        appStats[displayKey] = 0;
                                    appStats[displayKey] += scanIntervalSeconds;
                                }
                            }
                        }
                    }

                    // 4. Audio Playback Tracking
                    var audioPids = GetProcessesPlayingAudio();
                    foreach (var pid in audioPids)
                    {
                        var proc = allProcesses.FirstOrDefault(p => p.Id == pid);
                        if (proc != null)
                        {
                            string name = proc.ProcessName;
                            string windowTitle = "";
                            try { windowTitle = proc.MainWindowTitle; } catch {}

                            // If it's a browser playing audio but the sub-process has no window title, 
                            // find the main browser window process of the same name to retrieve the active tab title.
                            if (string.IsNullOrEmpty(windowTitle) &&
                                (name.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("opera", StringComparison.OrdinalIgnoreCase)))
                            {
                                var mainBrowser = allProcesses.FirstOrDefault(p =>
                                    p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrEmpty(p.MainWindowTitle));
                                if (mainBrowser != null)
                                {
                                    windowTitle = mainBrowser.MainWindowTitle;
                                }
                            }

                            string displayKey = GetDisplayKey(name, windowTitle);

                            string[] ignoredApps = { "Idle", "LockApp", "SearchUI" };
                            if (!ignoredApps.Contains(name, StringComparer.OrdinalIgnoreCase))
                            {
                                if (!audioStats.ContainsKey(displayKey))
                                    audioStats[displayKey] = 0;
                                audioStats[displayKey] += scanIntervalSeconds;
                            }
                        }
                    }

                    // 5. Periodic Screenshot to Discord
                    if ((DateTime.Now - lastScreenshotTime).TotalSeconds >= screenshotIntervalSeconds)
                    {
                        lastScreenshotTime = DateTime.Now;
                        await CaptureAndSendScreenshotAsync();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in main loop: {ex.Message}");
                }

                await Task.Delay(scanIntervalSeconds * 1000);
                loops++;

                int currentLoopsNeeded = (int)Math.Ceiling((double)reportIntervalSeconds / scanIntervalSeconds);
                if (loops >= currentLoopsNeeded)
                {
                    // Refresh block lists
                    await RefreshBlockListsAsync();

                    // Send Reports
                    await SendReportsAsync();

                    // Reset stats
                    appStats.Clear();
                    audioStats.Clear();
                    loops = 0;
                }
            }
        }

        private static string GetDisplayKey(string processName, string windowTitle)
        {
            string displayKey = processName;
            if (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(windowTitle))
            {
                displayKey = "chrome - " + Regex.Replace(windowTitle, @" - Google Chrome.*$", "");
            }
            else if (processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(windowTitle))
            {
                displayKey = "firefox - " + Regex.Replace(windowTitle, @" - Mozilla Firefox.*$", "");
            }
            else if (processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(windowTitle))
            {
                displayKey = "msedge - " + Regex.Replace(windowTitle, @" - Microsoft Edge.*$", "");
            }
            return displayKey;
        }

        private static async Task RefreshBlockListsAsync()
        {
            try
            {
                var response = await httpClient.GetStringAsync(BlockListGistUrl);
                if (!string.IsNullOrEmpty(response))
                {
                    using (JsonDocument doc = JsonDocument.Parse(response))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("blockedProcessNames", out var processNamesElement))
                        {
                            blockedProcessNames = processNamesElement.EnumerateArray()
                                .Select(x => x.GetString())
                                .Where(x => !string.IsNullOrEmpty(x))
                                .ToList();
                        }
                        if (root.TryGetProperty("blockedPageTitles", out var pageTitlesElement))
                        {
                            blockedPageTitles = pageTitlesElement.EnumerateArray()
                                .Select(x => x.GetString())
                                .Where(x => !string.IsNullOrEmpty(x))
                                .ToList();
                        }
                        if (root.TryGetProperty("downloadUrl", out var downloadUrlElement))
                        {
                            updateUrl = downloadUrlElement.GetString();
                        }
                        if (root.TryGetProperty("scanIntervalSeconds", out var scanElement) && scanElement.ValueKind == JsonValueKind.Number)
                        {
                            scanIntervalSeconds = scanElement.GetInt32();
                            if (scanIntervalSeconds <= 0) scanIntervalSeconds = 5;
                        }
                        if (root.TryGetProperty("reportIntervalSeconds", out var reportElement) && reportElement.ValueKind == JsonValueKind.Number)
                        {
                            reportIntervalSeconds = reportElement.GetInt32();
                            if (reportIntervalSeconds <= 0) reportIntervalSeconds = 360;
                        }
                        if (root.TryGetProperty("screenshotIntervalSeconds", out var screenshotElement) && screenshotElement.ValueKind == JsonValueKind.Number)
                        {
                            screenshotIntervalSeconds = screenshotElement.GetInt32();
                            if (screenshotIntervalSeconds <= 0) screenshotIntervalSeconds = 60;
                        }
                        if (root.TryGetProperty("intervals", out var intervalsElement) && intervalsElement.ValueKind == JsonValueKind.Array)
                        {
                            var newIntervals = new List<TimeInterval>();
                            foreach (var intervalElement in intervalsElement.EnumerateArray())
                            {
                                try
                                {
                                    string startStr = intervalElement.GetProperty("start").GetString();
                                    string endStr = intervalElement.GetProperty("end").GetString();
                                    string typeStr = intervalElement.GetProperty("type").GetString();

                                    if (TimeSpan.TryParse(startStr, out TimeSpan start) && TimeSpan.TryParse(endStr, out TimeSpan end))
                                    {
                                        newIntervals.Add(new TimeInterval { Start = start, End = end, Type = typeStr });
                                    }
                                }
                                catch { /* Ignore malformed intervals */ }
                            }
                            configuredIntervals = newIntervals;
                            SaveIntervalsToRegistry();
                        }
                        if (root.TryGetProperty("version", out var versionElement))
                        {
                            string remoteVersion = versionElement.GetString();
                            string localVersion = GetLocalVersion();
                            if (!string.IsNullOrEmpty(remoteVersion) && remoteVersion != localVersion)
                            {
                                Console.WriteLine($"New version detected: {remoteVersion} (Local: {localVersion}). Starting auto-update...");
                                await UpdateApplicationAsync(remoteVersion);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch block lists from gist: {ex.Message}");
            }
        }

        private static async Task SendReportsAsync()
        {
            try
            {
                var data = new List<object>();

                // Build Discord message
                StringBuilder message = new StringBuilder();
                message.AppendLine("**Raport Activitate**");
                message.AppendLine($"**Utilizator:** {currentUser}");
                message.AppendLine($"**Ora:** {DateTime.Now:HH:mm}");
                message.AppendLine();

                // Foreground active stats
                message.AppendLine("**Timp Prim-plan:**");
                var sortedAppStats = appStats.OrderByDescending(x => x.Value).ToList();
                bool fgFound = false;
                foreach (var stat in sortedAppStats)
                {
                    if (stat.Value >= 6)
                    {
                        fgFound = true;
                        message.AppendLine($"- **{stat.Key}**: {stat.Value} secunde");
                    }
                }
                if (!fgFound)
                {
                    message.AppendLine("- nici o activitate detectată");
                }

                // Audio stats
                message.AppendLine();
                message.AppendLine("**Timp Audio (Fundal/Prim-plan):**");
                var sortedAudioStats = audioStats.OrderByDescending(x => x.Value).ToList();
                bool audioFound = false;
                foreach (var stat in sortedAudioStats)
                {
                    if (stat.Value >= 6)
                    {
                        audioFound = true;
                        message.AppendLine($"- **{stat.Key}**: {stat.Value} secunde");
                    }
                }
                if (!audioFound)
                {
                    message.AppendLine("- nici un sunet detectat");
                }

                // Prepare Google Sheets Data (merged by app/display name)
                var allKeys = appStats.Keys.Union(audioStats.Keys).ToList();
                foreach (var key in allKeys)
                {
                    int fgSeconds = appStats.ContainsKey(key) ? appStats[key] : 0;
                    int audSeconds = audioStats.ContainsKey(key) ? audioStats[key] : 0;

                    if (fgSeconds >= 6 || audSeconds >= 6)
                    {
                        if (audSeconds >= 6)
                        {
                            data.Add(new { appName = key, seconds = fgSeconds, audioSeconds = audSeconds, user = currentUser });
                        }
                        else
                        {
                            data.Add(new { appName = key, seconds = fgSeconds, user = currentUser });
                        }
                    }
                }

                // Send Google Sheets report
                if (data.Count > 0)
                {
                    string sheetPayload = JsonSerializer.Serialize(data);
                    var content = new StringContent(sheetPayload, Encoding.UTF8, "application/json");
                    await PostWithRedirectAsync(GoogleWebhookUrl, content);
                }

                // Send Discord report
                var discordPayload = new { content = message.ToString() };
                string discordJson = JsonSerializer.Serialize(discordPayload);
                var discordContent = new StringContent(discordJson, Encoding.UTF8, "application/json");
                await httpClient.PostAsync(TextWebhookUrl, discordContent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send reports: {ex.Message}");
            }
        }

        private static async Task<HttpResponseMessage> PostWithRedirectAsync(string url, HttpContent content)
        {
            string currentUrl = url;
            HttpResponseMessage response = null;
            int redirectCount = 0;
            const int maxRedirects = 5;

            while (redirectCount < maxRedirects)
            {
                var jsonString = await content.ReadAsStringAsync();
                var newContent = new StringContent(jsonString, Encoding.UTF8, "application/json");

                response = await redirectHttpClient.PostAsync(currentUrl, newContent);

                if (response.StatusCode == System.Net.HttpStatusCode.Redirect ||
                    response.StatusCode == System.Net.HttpStatusCode.Found ||
                    response.StatusCode == System.Net.HttpStatusCode.SeeOther ||
                    response.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect ||
                    response.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
                {
                    var redirectUrl = response.Headers.Location;
                    if (redirectUrl != null)
                    {
                        currentUrl = redirectUrl.IsAbsoluteUri ? redirectUrl.AbsoluteUri : new Uri(new Uri(currentUrl), redirectUrl).AbsoluteUri;
                        redirectCount++;
                        continue;
                    }
                }
                break;
            }
            return response;
        }

        private static async Task CaptureAndSendScreenshotAsync()
        {
            try
            {
                int width = GetSystemMetrics(SM_CXSCREEN);
                int height = GetSystemMetrics(SM_CYSCREEN);

                if (width <= 0 || height <= 0)
                {
                    Console.WriteLine("Invalid screen metrics retrieved.");
                    return;
                }

                byte[] imageBytes;
                using (Bitmap bitmap = new Bitmap(width, height))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, new Size(width, height));
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        bitmap.Save(ms, ImageFormat.Png);
                        imageBytes = ms.ToArray();
                    }
                }

                using (var content = new MultipartFormDataContent())
                {
                    var payload = new 
                    { 
                        content = $"**Monitorizare Ecran**\n**Utilizator:** {currentUser}\n**Ora:** {DateTime.Now:HH:mm:ss}" 
                    };
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    content.Add(new StringContent(jsonPayload, Encoding.UTF8, "application/json"), "payload_json");

                    var imageContent = new ByteArrayContent(imageBytes);
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    content.Add(imageContent, "file", "screenshot.png");

                    var response = await httpClient.PostAsync(ImageWebhookUrl, content);
                    if (!response.IsSuccessStatusCode)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"Discord screenshot upload failed: {response.StatusCode} - {responseBody}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to capture or send screenshot: {ex.Message}");
            }
        }

        private static async Task UpdateApplicationAsync(string remoteVersion)
        {
            try
            {
                if (string.IsNullOrEmpty(updateUrl))
                {
                    Console.WriteLine("Update URL is not set. Aborting update.");
                    return;
                }

                string currentExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(currentExe))
                {
                    Console.WriteLine("Could not determine current executable path.");
                    return;
                }

                string backupExe = currentExe + ".bak";
                string newExe = currentExe + ".new";

                Console.WriteLine($"Downloading update from {updateUrl} to {newExe}...");

                // Download the file (automatically follows redirects for public GitHub releases)
                using (var response = await httpClient.GetAsync(updateUrl))
                {
                    response.EnsureSuccessStatusCode();

                    using (var fs = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }

                // Verify the downloaded file is not empty and is a valid Windows Executable
                FileInfo fi = new FileInfo(newExe);
                if (!fi.Exists || fi.Length == 0)
                {
                    Console.WriteLine("Downloaded file is empty or missing. Aborting update.");
                    if (File.Exists(newExe)) File.Delete(newExe);
                    return;
                }

                // Bulletproof check: Ensure file starts with 'MZ' (Windows Executable header)
                bool isValidExecutable = false;
                using (var fs = new FileStream(newExe, FileMode.Open, FileAccess.Read))
                {
                    if (fs.Length >= 2)
                    {
                        int byte1 = fs.ReadByte();
                        int byte2 = fs.ReadByte();
                        if (byte1 == 0x4D && byte2 == 0x5A) // 'M' 'Z'
                        {
                            isValidExecutable = true;
                        }
                    }
                }

                if (!isValidExecutable)
                {
                    Console.WriteLine("Downloaded file is not a valid executable (likely an HTML error page from Google Drive). Aborting update.");
                    if (File.Exists(newExe)) File.Delete(newExe);
                    return;
                }

                // Unblock the downloaded file to bypass Windows SmartScreen warnings on next launch
                try
                {
                    string zoneFile = newExe + ":Zone.Identifier";
                    DeleteFile(zoneFile);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to remove Zone.Identifier from updated file: {ex.Message}");
                }

                Console.WriteLine("Applying update...");

                // Rename running to .bak, and .new to original name
                if (File.Exists(backupExe))
                {
                    File.Delete(backupExe);
                }

                File.Move(currentExe, backupExe);
                File.Move(newExe, currentExe);

                // Update the local version in the registry so it doesn't loop
                SetLocalVersion(remoteVersion);

                Console.WriteLine("Update applied successfully. Restarting...");

                // Restart the process
                string[] args = Environment.GetCommandLineArgs();
                string arguments = string.Join(" ", args.Skip(1));

                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    Arguments = arguments,
                    UseShellExecute = true
                });

                // Exit current process
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update failed: {ex.Message}");
            }
        }

        private static List<uint> GetProcessesPlayingAudio()
        {
            var activeProcessIds = new List<uint>();
            try
            {
                IMMDeviceEnumerator deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice speakers;
                deviceEnumerator.GetDefaultAudioEndpoint(0, 1, out speakers); // eRender = 0, eMultimedia = 1
                if (speakers == null) return activeProcessIds;

                Guid IID_IAudioSessionManager2 = typeof(IAudioSessionManager2).GUID;
                object o;
                speakers.Activate(ref IID_IAudioSessionManager2, 1, IntPtr.Zero, out o); // CLSCTX_INPROC_SERVER = 1
                IAudioSessionManager2 manager = (IAudioSessionManager2)o;

                IAudioSessionEnumerator sessionEnumerator;
                manager.GetSessionEnumerator(out sessionEnumerator);
                
                int count;
                sessionEnumerator.GetCount(out count);

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 session;
                    sessionEnumerator.GetSession(i, out session);
                    
                    if (session != null)
                    {
                        IAudioMeterInformation meter = session as IAudioMeterInformation;
                        if (meter != null)
                        {
                            float peak;
                            meter.GetPeakValue(out peak);
                            if (peak > 0)
                            {
                                uint pid;
                                session.GetProcessId(out pid);
                                if (pid > 0 && !activeProcessIds.Contains(pid))
                                {
                                    activeProcessIds.Add(pid);
                                }
                            }
                        }
                        Marshal.ReleaseComObject(session);
                    }
                }
                if (sessionEnumerator != null) Marshal.ReleaseComObject(sessionEnumerator);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (speakers != null) Marshal.ReleaseComObject(speakers);
            }
            catch { /* Ignore exceptions */ }
            return activeProcessIds;
        }

        private static string GetLocalVersion()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\MonitorApp"))
                {
                    var val = key.GetValue("Version");
                    if (val != null) return val.ToString();
                    
                    // Default first-run version
                    key.SetValue("Version", "1.0.0");
                    return "1.0.0";
                }
            }
            catch
            {
                return "1.0.0";
            }
        }

        private static void SetLocalVersion(string version)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\MonitorApp"))
                {
                    key.SetValue("Version", version);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save version to registry: {ex.Message}");
            }
        }

        private static void SaveIntervalsToRegistry()
        {
            try
            {
                string json = JsonSerializer.Serialize(configuredIntervals);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\MonitorApp"))
                {
                    key.SetValue("Intervals", json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save intervals to registry: {ex.Message}");
            }
        }

        private static void LoadIntervalsFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\MonitorApp"))
                {
                    var val = key.GetValue("Intervals");
                    if (val != null)
                    {
                        var intervals = JsonSerializer.Deserialize<List<TimeInterval>>(val.ToString());
                        if (intervals != null)
                        {
                            configuredIntervals = intervals;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load intervals from registry: {ex.Message}");
            }
        }

        private static void ProtectProcess()
        {
            try
            {
                var hProcess = Process.GetCurrentProcess().Handle;
                uint len = 0;
                // Get required size (DACL_SECURITY_INFORMATION = 4)
                GetKernelObjectSecurity(hProcess, 4, null, 0, out len);
                
                if (len > 0)
                {
                    byte[] sd = new byte[len];
                    if (GetKernelObjectSecurity(hProcess, 4, sd, len, out len))
                    {
                        var dacl = new RawSecurityDescriptor(sd, 0);
                        var currentUserSid = WindowsIdentity.GetCurrent().User;
                        
                        // Deny PROCESS_TERMINATE (0x0001) to the current user
                        dacl.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x0001, currentUserSid, false, null));
                        
                        byte[] newSd = new byte[dacl.BinaryLength];
                        dacl.GetBinaryForm(newSd, 0);
                        SetKernelObjectSecurity(hProcess, 4, newSd);
                        Console.WriteLine("Process termination protection applied.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply process protection: {ex.Message}");
            }
        }
    }
}

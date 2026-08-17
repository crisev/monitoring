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



 *  TODO: 
 *  - Remote Config Option A (Discord Bot): Interactive buttons/commands via Cloudflare Worker + GitHub Gist API (GitHub PAT with gist scope; 0 changes to Monitor.exe).
 *  - Remote Config Option B (Mobile Web App): React/Next.js UI backed by Firebase Realtime Database (public read for Monitor.exe, email/pass auth write for admin).
 
 * ======================================================================================
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseDesktop(IntPtr hDesktop);

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
            "firefox",
            "tor",
            "tor-browser",
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

        private static void ApplyWindowsTimeRegistryRestrictions()
        {
            try
            {
                // 1. Hide Date & Time page in Windows Settings app (Windows 10/11)
                string[] explorerKeys = new string[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                    @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Policies\Explorer"
                };

                foreach (var subKeyPath in explorerKeys)
                {
                    try
                    {
                        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(subKeyPath))
                        {
                            if (key != null)
                            {
                                key.SetValue("SettingsPageVisibility", "hide:dateandtime", RegistryValueKind.String);
                                key.SetValue("NoSetTime", 1, RegistryValueKind.DWord);
                            }
                        }
                        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath))
                        {
                            if (key != null)
                            {
                                key.SetValue("SettingsPageVisibility", "hide:dateandtime", RegistryValueKind.String);
                                key.SetValue("NoSetTime", 1, RegistryValueKind.DWord);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Registry restriction notice ({subKeyPath}): {ex.Message}");
                    }
                }

                // 2. Disable Date and Time Control Panel Applet (timedate.cpl)
                string[] controlPanelKeys = new string[]
                {
                    @"SOFTWARE\Policies\Microsoft\Windows\Control Panel",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"
                };

                foreach (var subKeyPath in controlPanelKeys)
                {
                    try
                    {
                        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(subKeyPath))
                        {
                            if (key != null)
                            {
                                key.SetValue("NoDateAndTimeUI", 1, RegistryValueKind.DWord);
                                key.SetValue("NoSetTime", 1, RegistryValueKind.DWord);
                            }
                        }
                        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath))
                        {
                            if (key != null)
                            {
                                key.SetValue("NoDateAndTimeUI", 1, RegistryValueKind.DWord);
                                key.SetValue("NoSetTime", 1, RegistryValueKind.DWord);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Registry restriction notice ({subKeyPath}): {ex.Message}");
                    }
                }

                Console.WriteLine("Windows Date & Time Registry policies applied successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply Windows Time Registry policies: {ex.Message}");
            }
        }

        private static readonly Stopwatch networkStopwatch = new Stopwatch();
        private static readonly Stopwatch offlineSessionStopwatch = Stopwatch.StartNew();
        private static DateTime syncedUtcTime = DateTime.MinValue;
        private static bool isNetworkTimeSynced = false;
        private static readonly object timeSyncLock = new object();
        private static TimeZoneInfo bucharestTimeZoneInfo = GetBucharestTimeZoneInfo();
        private static readonly Stopwatch screenshotStopwatch = Stopwatch.StartNew();

        private static readonly string[] ntpServers = new string[]
        {
            "time.google.com",
            "pool.ntp.org",
            "time.windows.com",
            "time.cloudflare.com"
        };

        private static readonly string[] httpTimeUrls = new string[]
        {
            "https://www.google.com",
            "https://www.cloudflare.com",
            "https://api.github.com",
            "https://www.microsoft.com"
        };

        public static bool IsNetworkTimeSynced => isNetworkTimeSynced;

        private static TimeZoneInfo GetBucharestTimeZoneInfo()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
            }
            catch
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("GTB Standard Time");
                }
                catch
                {
                    return TimeZoneInfo.CreateCustomTimeZone("Bucharest Custom", TimeSpan.FromHours(2), "Bucharest Time", "Bucharest Time");
                }
            }
        }

        private static async Task<DateTime?> QueryNtpServerAsync(string ntpServer, int timeoutMs = 2500)
        {
            try
            {
                using (var udpClient = new UdpClient())
                {
                    udpClient.Client.ReceiveTimeout = timeoutMs;
                    udpClient.Client.SendTimeout = timeoutMs;

                    var ntpData = new byte[48];
                    ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (Client)

                    var addresses = await Dns.GetHostAddressesAsync(ntpServer);
                    var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                    if (ip == null) return null;

                    var ipEndPoint = new IPEndPoint(ip, 123);
                    await udpClient.SendAsync(ntpData, ntpData.Length, ipEndPoint);

                    var receiveTask = udpClient.ReceiveAsync();
                    if (await Task.WhenAny(receiveTask, Task.Delay(timeoutMs)) == receiveTask)
                    {
                        var result = receiveTask.Result;
                        if (result.Buffer != null && result.Buffer.Length >= 48)
                        {
                            ulong intPart = (ulong)result.Buffer[40] << 24 | (ulong)result.Buffer[41] << 16 | (ulong)result.Buffer[42] << 8 | (ulong)result.Buffer[43];
                            ulong fractPart = (ulong)result.Buffer[44] << 24 | (ulong)result.Buffer[45] << 16 | (ulong)result.Buffer[46] << 8 | (ulong)result.Buffer[47];

                            var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
                            var networkDateTime = (new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).AddMilliseconds((long)milliseconds);

                            if (networkDateTime.Year >= 2024 && networkDateTime.Year <= 2035)
                            {
                                return networkDateTime;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<DateTime?> QueryHttpDateAsync(string url, int timeoutMs = 2500)
        {
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                {
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                    var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (response.Headers.Date.HasValue)
                    {
                        var dt = response.Headers.Date.Value.UtcDateTime;
                        if (dt.Year >= 2024 && dt.Year <= 2035)
                        {
                            return dt;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static async Task<bool> SyncNetworkTimeAsync()
        {
            // 1. Try NTP servers first
            foreach (var ntpServer in ntpServers)
            {
                var ntpTime = await QueryNtpServerAsync(ntpServer);
                if (ntpTime.HasValue)
                {
                    ApplyNetworkTimeSync(ntpTime.Value, $"NTP ({ntpServer})");
                    return true;
                }
            }

            // 2. Fallback to HTTP HEAD Date header
            foreach (var url in httpTimeUrls)
            {
                var httpTime = await QueryHttpDateAsync(url);
                if (httpTime.HasValue)
                {
                    ApplyNetworkTimeSync(httpTime.Value, $"HTTP ({url})");
                    return true;
                }
            }

            return false;
        }

        private static void ApplyNetworkTimeSync(DateTime utcTime, string source)
        {
            lock (timeSyncLock)
            {
                syncedUtcTime = utcTime;
                networkStopwatch.Restart();
                isNetworkTimeSynced = true;
            }

            SaveVerifiedTimeWatermark(utcTime);
            Console.WriteLine($"[Time Sync] Successfully synchronized true UTC time from {source}: {utcTime:yyyy-MM-dd HH:mm:ss} UTC (Bucharest: {GetTrueBucharestTime():yyyy-MM-dd HH:mm:ss})");
        }

        private static void SaveVerifiedTimeWatermark(DateTime utcTime)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\MonitorApp"))
                {
                    key.SetValue("LastVerifiedUtcTime", utcTime.ToString("o"));
                    var maxVal = key.GetValue("MaxRecordedUtcTime");
                    if (maxVal == null || !DateTime.TryParse(maxVal.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime maxDt) || utcTime > maxDt)
                    {
                        key.SetValue("MaxRecordedUtcTime", utcTime.ToString("o"));
                    }
                }
            }
            catch { }
        }

        private static DateTime GetLastVerifiedUtcWatermark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\MonitorApp"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("LastVerifiedUtcTime");
                        if (val != null && DateTime.TryParse(val.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt))
                        {
                            return dt;
                        }
                        var maxVal = key.GetValue("MaxRecordedUtcTime");
                        if (maxVal != null && DateTime.TryParse(maxVal.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime maxDt))
                        {
                            return maxDt;
                        }
                    }
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        private static async Task RunPeriodicNetworkTimeSyncLoopAsync()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    await SyncNetworkTimeAsync();
                }
                catch { }
            }
        }

        private static void SyncNetworkTimeFromResponse(HttpResponseMessage response)
        {
            try
            {
                if (response != null && response.Headers != null && response.Headers.Date.HasValue)
                {
                    var dt = response.Headers.Date.Value.UtcDateTime;
                    if (dt.Year >= 2024 && dt.Year <= 2035)
                    {
                        lock (timeSyncLock)
                        {
                            syncedUtcTime = dt;
                            networkStopwatch.Restart();
                            isNetworkTimeSynced = true;
                        }
                        SaveVerifiedTimeWatermark(dt);
                    }
                }
            }
            catch { }
        }

        private static DateTime GetTrueUtcTime()
        {
            lock (timeSyncLock)
            {
                if (isNetworkTimeSynced && networkStopwatch.IsRunning)
                {
                    return syncedUtcTime.Add(networkStopwatch.Elapsed);
                }
            }

            // Fallback when network is not yet verified (e.g. offline boot)
            DateTime watermark = GetLastVerifiedUtcWatermark();
            DateTime localUtc = DateTime.UtcNow;

            // Anti-tamper check: If local BIOS clock was set backwards before our last verified time,
            // reject the local clock and advance monotonically from the last verified watermark.
            if (watermark > DateTime.MinValue)
            {
                if (localUtc < watermark)
                {
                    return watermark.Add(offlineSessionStopwatch.Elapsed);
                }
            }

            return localUtc;
        }

        private static DateTime GetTrueBucharestTime()
        {
            DateTime utc = GetTrueUtcTime();
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(utc, bucharestTimeZoneInfo);
            }
            catch
            {
                return utc.AddHours(2);
            }
        }

        private static void KillBlockedProcesses()
        {
            try
            {
                var allProcesses = Process.GetProcesses();
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

                        if (blockedProcessNames.Contains(procName, StringComparer.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"BLOCKED: Process '{procName}' is forbidden - killing.");
                            proc.Kill(true);
                            isBlocked = true;
                        }

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
            catch { }
        }

        private static async Task InitiateContinuousShutdownAsync(string reason)
        {
            Console.WriteLine($"Initiating continuous shutdown sequence: {reason}");
            try
            {
                await SendDailyReportAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send daily report before shutdown: {ex.Message}");
            }
            await SendDiscordNotificationAsync($"🔴 **Application Shutting Down**\n- **User:** `{currentUser}`\n- **Reason:** `{reason}`\n- **Time:** `{GetTrueBucharestTime():HH:mm:ss}`");

            if (!isDebugMode && !noShutdown)
            {
                string shutdownExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
                while (true)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(shutdownExe, "/s /f /t 1") { CreateNoWindow = true, UseShellExecute = false });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to initiate shutdown: {ex.Message}");
                    }
                    KillBlockedProcesses();
                    await Task.Delay(1000);
                }
            }
            else
            {
                Console.WriteLine("[DEBUG] Shutdown simulated since debug/no-shutdown mode is active.");
                Environment.Exit(0);
            }
        }

        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly HttpClient redirectHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        static Program()
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            redirectHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        private static readonly string currentUser = Environment.UserName;

        private static int scanIntervalSeconds = 5;
        private static int reportIntervalSeconds = 360;
        private static int screenshotIntervalSeconds = 60;
        private static int dailyGameTimeMinutes = 0;
        private static int maxScreenTimeMinutes = 0;
        private static int dailyReportIntervalMinutes = 30;
        private static readonly Stopwatch dailyReportStopwatch = Stopwatch.StartNew();
        private static DailyStatsData currentDailyStats = new DailyStatsData();
        private static bool gameQuotaExceededNotified = false;
        private static bool gameTenMinutesWarningNotified = false;
        private static bool intervalTenMinutesWarningNotified = false;
        private static bool screenTenMinutesWarningNotified = false;
        private static bool screenFiveMinutesWarningNotified = false;
        private static bool screenOneMinuteWarningNotified = false;

        private static Mutex singleInstanceMutex = new Mutex(true, "{8F6F0AC4-B9A1-45fd-A8CF-72F04E6BDE8F}");

        private static List<TimeInterval> configuredIntervals = new List<TimeInterval>();
        private static bool isGamingModeActive = false;
        private static bool wasInInterval = false;
        private static bool isDebugMode = false;
        private static bool noShutdown = false;
        private static bool forceUpdate = false;

        public static bool IsGamingModeActive => isGamingModeActive;

        private static bool IsSessionLocked()
        {
            try
            {
                // 1. Check if user input desktop is accessible
                IntPtr hDesktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
                if (hDesktop == IntPtr.Zero)
                {
                    // User desktop is not the active input desktop (locked, login screen, or UAC desktop)
                    return true;
                }
                CloseDesktop(hDesktop);

                // 2. Check foreground window process name
                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == IntPtr.Zero)
                {
                    return false;
                }

                uint pid = 0;
                GetWindowThreadProcessId(fgHwnd, out pid);
                if (pid > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById((int)pid);
                        string procName = proc.ProcessName;
                        if (procName.Equals("LockApp", StringComparison.OrdinalIgnoreCase) ||
                            procName.Equals("LogonUI", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void ToggleGamingMode()
        {
            SetGamingMode(!isGamingModeActive);
        }

        public static void SetGamingMode(bool enable)
        {
            EnsureCurrentDayStats();
            if (enable)
            {
                int remainingSeconds = currentDailyStats.AvailableGamingSeconds - currentDailyStats.TotalGamingSeconds;
                if (remainingSeconds <= 0 && currentDailyStats.AvailableGamingSeconds > 0)
                {
                    TrayService.Instance?.ShowNotification("Gaming Time Expired", "Today's gaming quota has already been spent.", ToolTipIcon.Warning);
                    return;
                }
                if (remainingSeconds > 600)
                {
                    gameTenMinutesWarningNotified = false;
                }
                isGamingModeActive = true;
                Console.WriteLine("Gaming Mode turned ON by user.");
                TrayService.Instance?.ShowNotification("🎮 Gaming Mode ACTIVATED", $"Gaming session started. Remaining time: {remainingSeconds / 60} minute(s).", ToolTipIcon.Info);
            }
            else
            {
                isGamingModeActive = false;
                Console.WriteLine("Gaming Mode turned OFF by user. Enforcing School mode.");
                KillBlockedProcesses();
                TrayService.Instance?.ShowNotification("🔵 School Mode ACTIVATED", "Gaming session stopped. Restricted applications are blocked.", ToolTipIcon.Info);
            }

            TrayService.Instance?.UpdateStatus(GetIntervalDisplayText(), currentDailyStats.TotalGamingSeconds, currentDailyStats.AvailableGamingSeconds, currentDailyStats.TotalComputerSeconds, currentDailyStats.TotalScreenSeconds, maxScreenTimeMinutes, isGamingModeActive, currentDailyStats);
        }

        public static string GetIntervalDisplayText()
        {
            if (configuredIntervals == null || configuredIntervals.Count == 0) return "24/7 (No interval restrictions)";
            var first = configuredIntervals.First();
            return $"{first.Start:hh\\:mm} - {first.End:hh\\:mm}";
        }

        static async Task Main(string[] args)
        {
            isDebugMode = args.Contains("--ignore-intervals", StringComparer.OrdinalIgnoreCase) || 
                          args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
            noShutdown = args.Contains("--no-shutdown", StringComparer.OrdinalIgnoreCase);
            forceUpdate = args.Contains("--test-update", StringComparer.OrdinalIgnoreCase) || 
                          args.Contains("--force-update", StringComparer.OrdinalIgnoreCase);

            // Hide or allocate console window depending on --visible, isDebugMode, noShutdown or forceUpdate
            if (args.Contains("--visible", StringComparer.OrdinalIgnoreCase) || isDebugMode || noShutdown || forceUpdate)
            {
                AllocConsole();
                if (isDebugMode)
                {
                    Console.WriteLine("Debug / Ignore-Intervals mode is active. Bypassing shutdowns and interval exits.");
                }
                else if (noShutdown)
                {
                    Console.WriteLine("No-Shutdown mode is active. Bypassing actual shutdowns.");
                }
                else if (forceUpdate)
                {
                    Console.WriteLine("Force-Update / Test-Update mode is active. Triggering update checks immediately.");
                }
            }
            else
            {
                IntPtr consolePtr = GetConsoleWindow();
                if (consolePtr != IntPtr.Zero)
                {
                    ShowWindow(consolePtr, SW_HIDE);
                }
            }

            if (!singleInstanceMutex.WaitOne(TimeSpan.Zero, true))
            {
                Console.WriteLine("⚠️ Another instance of Monitor is already running in background (holding single instance lock).");
                Console.WriteLine("Please close the existing instance or kill 'Monitor.exe' in Task Manager.");
                return;
            }

            // Protect the process from being terminated by the current user (requires Admin to kill)
            ProtectProcess();

            // Enforce Windows Registry policies to disable Date & Time modification natively
            ApplyWindowsTimeRegistryRestrictions();

            int loops = 0;

            LoadIntervalsFromRegistry();

            // Actively synchronize network time from authoritative sources (NTP / HTTP) before initializing daily stats
            Console.WriteLine("Synchronizing network time from authoritative sources (NTP / HTTP)...");
            bool initialTimeSynced = false;
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                initialTimeSynced = await SyncNetworkTimeAsync();
                if (initialTimeSynced)
                {
                    break;
                }
                Console.WriteLine($"Network time sync attempt {attempt} failed (network initializing). Retrying in 2s...");
                await Task.Delay(2000);
            }

            if (!initialTimeSynced)
            {
                Console.WriteLine("⚠️ Warning: Initial network time sync failed. Using monotonic time anchored to last verified timestamp.");
            }

            // Start background periodic network time sync loop (every 5 minutes)
            _ = Task.Run(RunPeriodicNetworkTimeSyncLoopAsync);

            EnsureCurrentDayStats();

            // Start Windows System Tray Service (Runs in background STA thread)
            TrayService.Start();

            string localVer = GetLocalVersion();
            await SendDiscordNotificationAsync($"🟢 **Application Started**\n- **User:** `{currentUser}`\n- **Version:** `{localVer}`\n- **Time:** `{GetTrueBucharestTime():yyyy-MM-dd HH:mm:ss}` (Bucharest)");

            // Always try to fetch the latest configuration and intervals from the Gist first (retrying on startup in case network is initializing)
            bool gistFetched = false;
            int maxStartupRetries = 6;
            for (int attempt = 1; attempt <= maxStartupRetries; attempt++)
            {
                gistFetched = await RefreshBlockListsAsync();
                if (gistFetched)
                {
                    Console.WriteLine($"Successfully updated configuration from Gist on startup (attempt {attempt}).");
                    break;
                }

                if (attempt < maxStartupRetries)
                {
                    Console.WriteLine($"Startup Gist fetch attempt {attempt} failed (network initializing). Retrying in 5 seconds...");
                    await Task.Delay(5000);
                }
            }

            TimeSpan currentTime = GetTrueBucharestTime().TimeOfDay;
            var activeInterval = GetActiveInterval(currentTime);

            if (activeInterval == null && configuredIntervals.Count > 0 && !isDebugMode)
            {
                await InitiateContinuousShutdownAsync("No active time interval");
                return;
            }
            
            wasInInterval = activeInterval != null || isDebugMode;

            // Initial tray status update
            TrayService.Instance?.UpdateStatus(GetIntervalDisplayText(), currentDailyStats.TotalGamingSeconds, currentDailyStats.AvailableGamingSeconds, currentDailyStats.TotalComputerSeconds, currentDailyStats.TotalScreenSeconds, maxScreenTimeMinutes, isGamingModeActive, currentDailyStats);

            while (true)
            {
                try
                {
                    EnsureCurrentDayStats();

                    // Track total computer on time
                    currentDailyStats.TotalComputerSeconds += scanIntervalSeconds;

                    // Track screen time (only when not locked/on login screen)
                    bool isSessionLocked = IsSessionLocked();
                    if (!isSessionLocked)
                    {
                        currentDailyStats.TotalScreenSeconds += scanIntervalSeconds;
                    }

                    // Check for MaxScreenTimeMinutes limitation (10m, 5m, 1m warnings & shutdown)
                    if (maxScreenTimeMinutes > 0 && !isDebugMode)
                    {
                        int maxScreenSec = maxScreenTimeMinutes * 60;
                        int remainingScreenSec = maxScreenSec - currentDailyStats.TotalScreenSeconds;

                        // 10-minute Screen Time Warning
                        if (remainingScreenSec <= 600 && remainingScreenSec > 300 && !screenTenMinutesWarningNotified)
                        {
                            screenTenMinutesWarningNotified = true;
                            int remMin = (int)Math.Ceiling(remainingScreenSec / 60.0);
                            TrayService.Instance?.ShowNotification("⏰ 10 Minutes of Screen Time Left", $"You have {remMin} minute(s) of daily screen time left before computer shuts down.", ToolTipIcon.Warning);
                            Console.WriteLine($"[Screen Time] 10-minute warning triggered ({remMin}m left).");
                        }
                        else if (remainingScreenSec > 600)
                        {
                            screenTenMinutesWarningNotified = false;
                        }

                        // 5-minute Screen Time Warning
                        if (remainingScreenSec <= 300 && remainingScreenSec > 60 && !screenFiveMinutesWarningNotified)
                        {
                            screenFiveMinutesWarningNotified = true;
                            int remMin = (int)Math.Ceiling(remainingScreenSec / 60.0);
                            TrayService.Instance?.ShowNotification("⏰ 5 Minutes of Screen Time Left", $"Computer will shut down in {remMin} minute(s). Please save your work.", ToolTipIcon.Warning);
                            Console.WriteLine($"[Screen Time] 5-minute warning triggered ({remMin}m left).");
                        }
                        else if (remainingScreenSec > 300)
                        {
                            screenFiveMinutesWarningNotified = false;
                        }

                        // 1-minute Screen Time Warning
                        if (remainingScreenSec <= 60 && remainingScreenSec > 0 && !screenOneMinuteWarningNotified)
                        {
                            screenOneMinuteWarningNotified = true;
                            TrayService.Instance?.ShowNotification("⚠️ 1 Minute until PC Shutdown", "Daily maximum screen time reached. Computer will shut down in 1 minute!", ToolTipIcon.Error);
                            Console.WriteLine($"[Screen Time] 1-minute warning triggered ({remainingScreenSec}s left).");
                        }
                        else if (remainingScreenSec > 60)
                        {
                            screenOneMinuteWarningNotified = false;
                        }

                        // Enforce shutdown when screen time exceeded
                        if (currentDailyStats.TotalScreenSeconds >= maxScreenSec)
                        {
                            Console.WriteLine($"Daily maximum screen time limit reached ({currentDailyStats.TotalScreenSeconds / 60}m / {maxScreenTimeMinutes}m). Initiating shutdown.");
                            await InitiateContinuousShutdownAsync($"Daily maximum screen time limit reached ({maxScreenTimeMinutes}m)");
                            return;
                        }
                    }

                    TimeSpan now = GetTrueBucharestTime().TimeOfDay;
                    var currentActiveInterval = GetActiveInterval(now);
                    
                    if (currentActiveInterval == null && isDebugMode)
                    {
                        // Create a dummy interval for debugging
                        currentActiveInterval = new TimeInterval
                        {
                            Start = now.Subtract(TimeSpan.FromHours(1)),
                            End = now.Add(TimeSpan.FromHours(1)),
                            Type = "School"
                        };
                    }

                    bool isInInterval = currentActiveInterval != null || isDebugMode;

                    if (wasInInterval && !isInInterval)
                    {
                        Console.WriteLine("Interval finished. Performing a final check of Gist to see if interval was extended...");
                        
                        // Send final report before checking exit
                        try
                        {
                            await SendDailyReportAsync();
                        }
                        catch { }

                        // Wait for 5 seconds to ensure we aren't too early (and to let Gist cache clear)
                        await Task.Delay(5000);
                        await RefreshBlockListsAsync();

                        // Re-evaluate the active interval with fresh data
                        now = GetTrueBucharestTime().TimeOfDay;
                        currentActiveInterval = GetActiveInterval(now);
                        isInInterval = currentActiveInterval != null || isDebugMode;

                        if (!isInInterval)
                        {
                            Console.WriteLine("Interval finished and no other interval is active. Shutting down.");
                            await InitiateContinuousShutdownAsync("Active time interval finished");
                            return;
                        }
                        else
                        {
                            Console.WriteLine("Shutdown aborted! Interval was extended.");
                        }
                    }
                    wasInInterval = isInInterval;

                    // 1. Check for 10-minute Computer Interval Warning (before PC shutdown)
                    if (currentActiveInterval != null && !isDebugMode)
                    {
                        TimeSpan end = currentActiveInterval.End;
                        TimeSpan remainingInInterval;
                        if (end >= now)
                        {
                            remainingInInterval = end - now;
                        }
                        else
                        {
                            remainingInInterval = TimeSpan.FromHours(24) - now + end;
                        }

                        if (remainingInInterval.TotalMinutes <= 10 && remainingInInterval.TotalMinutes > 0 && !intervalTenMinutesWarningNotified)
                        {
                            intervalTenMinutesWarningNotified = true;
                            int remMinutes = (int)Math.Ceiling(remainingInInterval.TotalMinutes);
                            TrayService.Instance?.ShowNotification("⏰ 10 Minutes until PC Shutdown", $"Allowed computer time ends in {remMinutes} minute(s). Please save your work.", ToolTipIcon.Warning);
                            Console.WriteLine($"10-minute computer interval warning triggered ({remMinutes}m left).");
                        }
                        else if (remainingInInterval.TotalMinutes > 10)
                        {
                            intervalTenMinutesWarningNotified = false;
                        }
                    }

                    // 2. Gaming Mode & Process Restriction Enforcement
                    if (isGamingModeActive)
                    {
                        // Accumulate gaming time every scan interval while Gaming Mode is ON
                        currentDailyStats.TotalGamingSeconds += scanIntervalSeconds;

                        int remainingGamingSeconds = currentDailyStats.AvailableGamingSeconds - currentDailyStats.TotalGamingSeconds;

                        // 10-minute Gaming Warning Notification
                        if (currentDailyStats.AvailableGamingSeconds > 0 && remainingGamingSeconds <= 600 && remainingGamingSeconds > 0 && !gameTenMinutesWarningNotified)
                        {
                            gameTenMinutesWarningNotified = true;
                            int remainingMinutes = (int)Math.Ceiling(remainingGamingSeconds / 60.0);
                            TrayService.Instance?.ShowNotification("⏳ 10 Minutes of Gaming Left", $"You have {remainingMinutes} minute(s) of game time left before School Mode activates.", ToolTipIcon.Warning);
                            Console.WriteLine($"10-minute gaming warning triggered ({remainingGamingSeconds}s remaining).");
                        }

                        // Check if total gaming time has reached the allowed quota (daily + carry-over)
                        if (currentDailyStats.AvailableGamingSeconds > 0 && currentDailyStats.TotalGamingSeconds >= currentDailyStats.AvailableGamingSeconds)
                        {
                            isGamingModeActive = false;
                            KillBlockedProcesses();

                            TrayService.Instance?.ShowNotification("Gaming Time Expired", "Daily gaming quota reached. School Mode has been activated.", ToolTipIcon.Warning);

                            if (!gameQuotaExceededNotified)
                            {
                                gameQuotaExceededNotified = true;
                                Console.WriteLine($"Daily game quota reached ({currentDailyStats.TotalGamingSeconds / 60}m / {currentDailyStats.AvailableGamingSeconds / 60}m). Activating School mode restrictions.");
                                await SendDiscordNotificationAsync($"⏳ **Gaming Time Expired**\n- **User:** `{currentUser}`\n- **Time Used:** `{currentDailyStats.TotalGamingSeconds / 60}m` / `{currentDailyStats.AvailableGamingSeconds / 60}m`\n- **Status:** School Mode activated (gaming apps/sites are blocked).");
                            }
                        }
                    }
                    else
                    {
                        // In School Mode (Gaming Mode is OFF): actively terminate prohibited processes
                        KillBlockedProcesses();
                    }

                    var allProcesses = Process.GetProcesses();

                    // 2. Foreground Application Tracking
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
                                    // Daily app stats
                                    if (!currentDailyStats.AppSeconds.ContainsKey(displayKey))
                                        currentDailyStats.AppSeconds[displayKey] = 0;
                                    currentDailyStats.AppSeconds[displayKey] += scanIntervalSeconds;

                                    // Track breakdown for games / blocked sites
                                    if (IsGameOrBlockedActivity(name, windowTitle))
                                    {
                                        if (!currentDailyStats.GameSeconds.ContainsKey(displayKey))
                                            currentDailyStats.GameSeconds[displayKey] = 0;
                                        currentDailyStats.GameSeconds[displayKey] += scanIntervalSeconds;
                                    }
                                }
                            }
                        }
                    }

                    // 3. Audio Playback Tracking
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
                                // Daily audio stats
                                if (!currentDailyStats.AudioSeconds.ContainsKey(displayKey))
                                    currentDailyStats.AudioSeconds[displayKey] = 0;
                                currentDailyStats.AudioSeconds[displayKey] += scanIntervalSeconds;
                            }
                        }
                    }

                    // Persist daily stats to registry on each active scan
                    SaveDailyStatsToRegistry();

                    // Update System Tray UI & Tooltip
                    TrayService.Instance?.UpdateStatus(GetIntervalDisplayText(), currentDailyStats.TotalGamingSeconds, currentDailyStats.AvailableGamingSeconds, currentDailyStats.TotalComputerSeconds, currentDailyStats.TotalScreenSeconds, maxScreenTimeMinutes, isGamingModeActive, currentDailyStats);

                    // 4. Periodic Screenshot to Discord
                    if (screenshotStopwatch.Elapsed.TotalSeconds >= screenshotIntervalSeconds)
                    {
                        screenshotStopwatch.Restart();
                        await CaptureAndSendScreenshotAsync();
                    }

                    // 5. Periodic Daily Stats Report to Discord (e.g. every 30 minutes)
                    if (dailyReportStopwatch.Elapsed.TotalMinutes >= dailyReportIntervalMinutes)
                    {
                        dailyReportStopwatch.Restart();
                        await SendDailyReportAsync();
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
                    // Refresh block lists periodically from Gist
                    await RefreshBlockListsAsync();
                    loops = 0;
                }
            }
        }

        private static TimeInterval GetActiveInterval(TimeSpan now)
        {
            var activeIntervals = configuredIntervals.Where(i => i.IsActive(now)).ToList();
            if (!activeIntervals.Any()) return null;

            // Give priority to Gaming intervals if there is an overlap
            var gamingInterval = activeIntervals.FirstOrDefault(i => i.Type.Equals("Gaming", StringComparison.OrdinalIgnoreCase));
            if (gamingInterval != null)
            {
                return gamingInterval;
            }

            return activeIntervals.First();
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

        private static bool IsGameOrBlockedActivity(string processName, string windowTitle)
        {
            if (string.IsNullOrEmpty(processName)) return false;

            if (blockedProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(windowTitle))
            {
                foreach (var keyword in blockedPageTitles)
                {
                    if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static async Task<bool> RefreshBlockListsAsync()
        {
            try
            {
                string cacheBusterUrl = $"{BlockListGistUrl}?t={GetTrueUtcTime().Ticks}";
                using (var request = new HttpRequestMessage(HttpMethod.Get, cacheBusterUrl))
                {
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true,
                        NoStore = true
                    };

                    var httpResponse = await httpClient.SendAsync(request);
                    SyncNetworkTimeFromResponse(httpResponse);
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Gist request failed with status code: {httpResponse.StatusCode}");
                        return false;
                    }

                    string response = await httpResponse.Content.ReadAsStringAsync();
                    if (string.IsNullOrEmpty(response)) return false;

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
                        if (root.TryGetProperty("timeZone", out var timeZoneElement))
                        {
                            string tzStr = timeZoneElement.GetString();
                            if (!string.IsNullOrEmpty(tzStr))
                            {
                                try
                                {
                                    bucharestTimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(tzStr);
                                }
                                catch { }
                            }
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
                        if ((root.TryGetProperty("dailyGameTimeMinutes", out var gameTimeElement) || root.TryGetProperty("dailyGameTime", out gameTimeElement)) && gameTimeElement.ValueKind == JsonValueKind.Number)
                        {
                            int newGameTime = gameTimeElement.GetInt32();
                            if (newGameTime < 0) newGameTime = 0;

                            if (newGameTime != dailyGameTimeMinutes)
                            {
                                int oldGameTime = dailyGameTimeMinutes;
                                dailyGameTimeMinutes = newGameTime;
                                SaveIntervalsToRegistry();

                                if (currentDailyStats != null)
                                {
                                    if (currentDailyStats.AvailableGamingSeconds <= 0)
                                    {
                                        currentDailyStats.AvailableGamingSeconds = dailyGameTimeMinutes * 60;
                                    }
                                    else
                                    {
                                        int diffSeconds = (newGameTime - oldGameTime) * 60;
                                        currentDailyStats.AvailableGamingSeconds = Math.Max(0, currentDailyStats.AvailableGamingSeconds + diffSeconds);
                                    }

                                    int remainingSeconds = currentDailyStats.AvailableGamingSeconds - currentDailyStats.TotalGamingSeconds;
                                    if (remainingSeconds > 0)
                                    {
                                        gameQuotaExceededNotified = false;
                                        if (remainingSeconds > 600)
                                        {
                                            gameTenMinutesWarningNotified = false;
                                        }
                                    }

                                    SaveDailyStatsToRegistry();
                                    TrayService.Instance?.UpdateStatus(GetIntervalDisplayText(), currentDailyStats.TotalGamingSeconds, currentDailyStats.AvailableGamingSeconds, currentDailyStats.TotalComputerSeconds, currentDailyStats.TotalScreenSeconds, maxScreenTimeMinutes, isGamingModeActive, currentDailyStats);
                                    Console.WriteLine($"[Config Update] dailyGameTimeMinutes updated from {oldGameTime}m to {newGameTime}m. New available quota: {currentDailyStats.AvailableGamingSeconds / 60}m (spent: {currentDailyStats.TotalGamingSeconds / 60}m, remaining: {Math.Max(0, remainingSeconds) / 60}m).");
                                }
                            }
                            else if (currentDailyStats != null && currentDailyStats.AvailableGamingSeconds <= 0 && dailyGameTimeMinutes > 0)
                            {
                                currentDailyStats.AvailableGamingSeconds = dailyGameTimeMinutes * 60;
                                SaveDailyStatsToRegistry();
                                TrayService.Instance?.UpdateStatus(GetIntervalDisplayText(), currentDailyStats.TotalGamingSeconds, currentDailyStats.AvailableGamingSeconds, currentDailyStats.TotalComputerSeconds, currentDailyStats.TotalScreenSeconds, maxScreenTimeMinutes, isGamingModeActive, currentDailyStats);
                            }
                        }

                        if ((root.TryGetProperty("maxScreenTimeMinutes", out var maxScreenElement) || root.TryGetProperty("maxScreenTime", out maxScreenElement)) && maxScreenElement.ValueKind == JsonValueKind.Number)
                        {
                            int newScreenTime = maxScreenElement.GetInt32();
                            if (newScreenTime < 0) newScreenTime = 0;
                            if (newScreenTime != maxScreenTimeMinutes)
                            {
                                int oldScreenTime = maxScreenTimeMinutes;
                                maxScreenTimeMinutes = newScreenTime;
                                SaveIntervalsToRegistry();
                                TrayService.Instance?.UpdateStatus(GetIntervalDisplayText(), currentDailyStats.TotalGamingSeconds, currentDailyStats.AvailableGamingSeconds, currentDailyStats.TotalComputerSeconds, currentDailyStats.TotalScreenSeconds, maxScreenTimeMinutes, isGamingModeActive, currentDailyStats);
                                Console.WriteLine($"[Config Update] maxScreenTimeMinutes updated from {oldScreenTime}m to {newScreenTime}m.");
                            }
                        }

                        // Parse bonusMinutes / extraMinutes for today only
                        // Supported formats:
                        // "bonusMinutes": { "date": "2026-08-16", "minutes": 30 }
                        // or "extraGameTimeMinutes": 30, "extraGameTimeDate": "2026-08-16"
                        string todayDateStr = GetTrueBucharestTime().ToString("yyyy-MM-dd");
                        int bonusMinutesFromGist = 0;

                        if (root.TryGetProperty("bonusMinutes", out var bonusElement))
                        {
                            if (bonusElement.ValueKind == JsonValueKind.Object)
                            {
                                string bDate = bonusElement.TryGetProperty("date", out var dEl) ? dEl.GetString() : "";
                                if (bDate == todayDateStr && bonusElement.TryGetProperty("minutes", out var mEl) && mEl.ValueKind == JsonValueKind.Number)
                                {
                                    bonusMinutesFromGist = Math.Max(0, mEl.GetInt32());
                                }
                            }
                            else if (bonusElement.ValueKind == JsonValueKind.Number)
                            {
                                bonusMinutesFromGist = Math.Max(0, bonusElement.GetInt32());
                            }
                        }
                        else if (root.TryGetProperty("extraGameTimeMinutes", out var extraEl) && extraEl.ValueKind == JsonValueKind.Number)
                        {
                            string extraDate = root.TryGetProperty("extraGameTimeDate", out var edEl) ? edEl.GetString() : todayDateStr;
                            if (extraDate == todayDateStr)
                            {
                                bonusMinutesFromGist = Math.Max(0, extraEl.GetInt32());
                            }
                        }

                        int targetBonusSeconds = bonusMinutesFromGist * 60;
                        if (currentDailyStats != null && targetBonusSeconds != currentDailyStats.GrantedBonusSeconds)
                        {
                            int bonusDelta = targetBonusSeconds - currentDailyStats.GrantedBonusSeconds;
                            currentDailyStats.AvailableGamingSeconds = Math.Max(0, currentDailyStats.AvailableGamingSeconds + bonusDelta);
                            currentDailyStats.GrantedBonusSeconds = targetBonusSeconds;

                            int remainingSeconds = currentDailyStats.AvailableGamingSeconds - currentDailyStats.TotalGamingSeconds;
                            if (remainingSeconds > 0)
                            {
                                gameQuotaExceededNotified = false;
                                if (remainingSeconds > 600)
                                {
                                    gameTenMinutesWarningNotified = false;
                                }
                            }

                            SaveDailyStatsToRegistry();
                            TrayService.Instance?.UpdateStatus(GetIntervalDisplayText(), currentDailyStats.TotalGamingSeconds, currentDailyStats.AvailableGamingSeconds, currentDailyStats.TotalComputerSeconds, currentDailyStats.TotalScreenSeconds, maxScreenTimeMinutes, isGamingModeActive, currentDailyStats);
                            TrayService.Instance?.ShowNotification("Bonus Game Time", $"Received {bonusMinutesFromGist} bonus minute(s) for today! Remaining: {Math.Max(0, remainingSeconds) / 60}m", ToolTipIcon.Info);
                            Console.WriteLine($"[Config Update] Bonus minutes updated to {bonusMinutesFromGist}m for date {todayDateStr}. New available quota: {currentDailyStats.AvailableGamingSeconds / 60}m.");
                        }
                        if (root.TryGetProperty("dailyReportIntervalMinutes", out var dailyReportElement) && dailyReportElement.ValueKind == JsonValueKind.Number)
                        {
                            dailyReportIntervalMinutes = dailyReportElement.GetInt32();
                            if (dailyReportIntervalMinutes <= 0) dailyReportIntervalMinutes = 30;
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
                            bool isVersionMismatch = !string.IsNullOrEmpty(remoteVersion) && remoteVersion != localVersion;

                            if ((isVersionMismatch && !isDebugMode) || forceUpdate)
                            {
                                Console.WriteLine($"[(Update Check)] Remote Version: {remoteVersion} (Local: {localVersion}, ForceUpdate: {forceUpdate})");
                                Console.WriteLine("Starting auto-update...");
                                await UpdateApplicationAsync(remoteVersion);
                            }
                        }
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch block lists from gist: {ex.Message}");
                return false;
            }
        }

        private static async Task SendDiscordNotificationAsync(string messageText)
        {
            try
            {
                var payload = new { content = messageText };
                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(TextWebhookUrl, content);
                SyncNetworkTimeFromResponse(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send Discord notification: {ex.Message}");
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
                SyncNetworkTimeFromResponse(response);

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
                    IntPtr hdcSrc = GetDC(IntPtr.Zero);
                    if (hdcSrc != IntPtr.Zero)
                    {
                        try
                        {
                            using (Graphics g = Graphics.FromImage(bitmap))
                            {
                                IntPtr hdcDest = g.GetHdc();
                                try
                                {
                                    BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
                                }
                                finally
                                {
                                    g.ReleaseHdc(hdcDest);
                                }
                            }
                        }
                        finally
                        {
                            ReleaseDC(IntPtr.Zero, hdcSrc);
                        }
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
                        content = $"**Screen Monitoring**\n- **User:** `{currentUser}`\n- **Time:** `{GetTrueBucharestTime():HH:mm:ss}`" 
                    };
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    content.Add(new StringContent(jsonPayload, Encoding.UTF8, "application/json"), "payload_json");

                    var imageContent = new ByteArrayContent(imageBytes);
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    content.Add(imageContent, "file", "screenshot.png");

                    var response = await httpClient.PostAsync(ImageWebhookUrl, content);
                    SyncNetworkTimeFromResponse(response);
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
                await SendDiscordNotificationAsync($"📥 **Downloading Update**\n- **User:** `{currentUser}`\n- **Target Version:** `{remoteVersion}`\n- **URL:** `{updateUrl}`");

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
                await SendDiscordNotificationAsync($"💾 **Applying Update Executable**\n- **User:** `{currentUser}`\n- **Target Version:** `{remoteVersion}`\n- **Status:** Overwriting executable on disk and restarting process.");

                // Rename running to .bak, and .new to original name
                if (File.Exists(backupExe))
                {
                    File.Delete(backupExe);
                }

                File.Move(currentExe, backupExe);
                File.Move(newExe, currentExe);

                // Update the local version in the registry so it doesn't loop
                SetLocalVersion(remoteVersion);

                if (forceUpdate)
                {
                    Console.WriteLine("Update applied successfully! [Force-Update Mode] Bypassing process restart to prevent infinite loop. Exiting cleanly.");
                    Environment.Exit(0);
                }

                Console.WriteLine("Update applied successfully. Restarting...");

                // Release Mutex before starting process so new instance doesn't exit immediately
                try
                {
                    singleInstanceMutex?.ReleaseMutex();
                    singleInstanceMutex?.Dispose();
                }
                catch { }

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
                try
                {
                    var payload = new 
                    { 
                        content = $"**Update Failure on {currentUser}'s PC:**\n- **Error:** `{ex.Message}`\n- **Type:** `{ex.GetType().Name}`\n- **Path:** `{Environment.ProcessPath}`" 
                    };
                    string json = JsonSerializer.Serialize(payload);
                    await httpClient.PostAsync(TextWebhookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
                }
                catch { }
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
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\MonitorApp"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("Version");
                        if (val != null && !string.IsNullOrEmpty(val.ToString()))
                        {
                            return val.ToString();
                        }
                    }
                }

                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (version != null)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }
            }
            catch { }
            return "1.0.0";
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
                    key.SetValue("DailyGameTimeMinutes", dailyGameTimeMinutes);
                    key.SetValue("MaxScreenTimeMinutes", maxScreenTimeMinutes);
                    key.SetValue("DailyReportIntervalMinutes", dailyReportIntervalMinutes);
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
                    var gameTimeVal = key.GetValue("DailyGameTimeMinutes");
                    if (gameTimeVal != null && int.TryParse(gameTimeVal.ToString(), out int savedGameTime))
                    {
                        dailyGameTimeMinutes = savedGameTime;
                    }
                    var screenTimeVal = key.GetValue("MaxScreenTimeMinutes");
                    if (screenTimeVal != null && int.TryParse(screenTimeVal.ToString(), out int savedScreenTime) && savedScreenTime >= 0)
                    {
                        maxScreenTimeMinutes = savedScreenTime;
                    }
                    var reportVal = key.GetValue("DailyReportIntervalMinutes");
                    if (reportVal != null && int.TryParse(reportVal.ToString(), out int savedReportInterval) && savedReportInterval > 0)
                    {
                        dailyReportIntervalMinutes = savedReportInterval;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load intervals from registry: {ex.Message}");
            }
        }

        private static void EnsureCurrentDayStats()
        {
            string today = GetTrueBucharestTime().ToString("yyyy-MM-dd");

            // If already loaded for today, nothing to do
            if (currentDailyStats != null && currentDailyStats.Date == today)
            {
                return;
            }

            // 1. Try to load existing record for today from registry
            var existingToday = LoadDailyStatsForDate(today);
            if (existingToday != null)
            {
                currentDailyStats = existingToday;
                Console.WriteLine($"Loaded existing daily stats for {today}: Gaming={currentDailyStats.TotalGamingSeconds}s / {currentDailyStats.AvailableGamingSeconds}s, Screen={currentDailyStats.TotalScreenSeconds}s");
                return;
            }

            // 2. If no record for today exists yet:
            // Calculate rollover from the most recent prior day
            int baseDailySeconds = dailyGameTimeMinutes * 60;
            int availableSeconds = baseDailySeconds;

            var lastStats = GetMostRecentPastDailyStats(today);
            if (lastStats != null && !string.IsNullOrEmpty(lastStats.Date))
            {
                if (DateTime.TryParse(lastStats.Date, out DateTime lastDate) &&
                    DateTime.TryParse(today, out DateTime currentDate))
                {
                    int daysDiff = (currentDate.Date - lastDate.Date).Days;
                    if (daysDiff >= 1)
                    {
                        int prevAvailable = lastStats.AvailableGamingSeconds > 0 
                            ? lastStats.AvailableGamingSeconds 
                            : baseDailySeconds;
                        int prevUnspent = Math.Max(0, prevAvailable - lastStats.TotalGamingSeconds);
                        
                        // Carryover = unspent from last active day + today's base daily quota
                        // Capped at maximum 5x daily base
                        int accumulated = prevUnspent + baseDailySeconds;
                        int maxCap = 5 * baseDailySeconds;
                        availableSeconds = Math.Min(maxCap, accumulated);
                        Console.WriteLine($"Rollover calculated from {lastStats.Date}: {daysDiff} day(s) passed. Prev unspent: {prevUnspent}s ({prevUnspent/60}m), Today's base: {baseDailySeconds/60}m, New available: {availableSeconds}s ({availableSeconds/60}m, max cap: {maxCap/60}m)");
                    }
                }
            }

            Console.WriteLine($"Starting fresh daily stats for date: {today} with available gaming time: {availableSeconds / 60}m");
            currentDailyStats = new DailyStatsData
            {
                Date = today,
                TotalGamingSeconds = 0,
                TotalComputerSeconds = 0,
                TotalScreenSeconds = 0,
                AvailableGamingSeconds = availableSeconds,
                GrantedBonusSeconds = 0,
                AppSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                AudioSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                GameSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };
            gameQuotaExceededNotified = false;
            gameTenMinutesWarningNotified = false;
            intervalTenMinutesWarningNotified = false;
            screenTenMinutesWarningNotified = false;
            screenFiveMinutesWarningNotified = false;
            screenOneMinuteWarningNotified = false;
            isGamingModeActive = false; // Always start in School mode on new day/startup
            SaveDailyStatsToRegistry();
        }

        private static void SaveDailyStatsToRegistry()
        {
            try
            {
                if (currentDailyStats == null || string.IsNullOrEmpty(currentDailyStats.Date)) return;
                string json = JsonSerializer.Serialize(currentDailyStats);

                // 1. Save to date-specific subkey for immutable history
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\MonitorApp\Stats\{currentDailyStats.Date}"))
                {
                    key.SetValue("Data", json);
                    key.SetValue("Date", currentDailyStats.Date);
                }

                // 2. Save to root key for backwards compatibility
                using (RegistryKey rootKey = Registry.CurrentUser.CreateSubKey(@"Software\MonitorApp"))
                {
                    rootKey.SetValue("DailyStats", json);
                    rootKey.SetValue("DailyStatsDate", currentDailyStats.Date);
                    rootKey.SetValue("LastActiveDate", currentDailyStats.Date);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save daily stats to registry: {ex.Message}");
            }
        }

        private static DailyStatsData LoadDailyStatsForDate(string date)
        {
            try
            {
                // Try date-specific subkey first
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"Software\MonitorApp\Stats\{date}"))
                {
                    if (key != null)
                    {
                        var dataVal = key.GetValue("Data");
                        if (dataVal != null)
                        {
                            var loaded = JsonSerializer.Deserialize<DailyStatsData>(dataVal.ToString());
                            if (loaded != null && loaded.Date == date)
                            {
                                NormalizeStats(loaded);
                                return loaded;
                            }
                        }
                    }
                }

                // Fallback to root key if matching date
                using (RegistryKey rootKey = Registry.CurrentUser.OpenSubKey(@"Software\MonitorApp"))
                {
                    if (rootKey != null)
                    {
                        var dateVal = rootKey.GetValue("DailyStatsDate");
                        var statsVal = rootKey.GetValue("DailyStats");
                        if (dateVal != null && dateVal.ToString() == date && statsVal != null)
                        {
                            var loaded = JsonSerializer.Deserialize<DailyStatsData>(statsVal.ToString());
                            if (loaded != null && loaded.Date == date)
                            {
                                NormalizeStats(loaded);
                                return loaded;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load daily stats for {date}: {ex.Message}");
            }
            return null;
        }

        private static void NormalizeStats(DailyStatsData stats)
        {
            if (stats == null) return;
            if (stats.AppSeconds == null)
                stats.AppSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (stats.AudioSeconds == null)
                stats.AudioSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (stats.GameSeconds == null)
                stats.GameSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (stats.AvailableGamingSeconds <= 0 && dailyGameTimeMinutes > 0)
                stats.AvailableGamingSeconds = dailyGameTimeMinutes * 60;
        }

        private static DailyStatsData GetMostRecentPastDailyStats(string currentDateStr)
        {
            try
            {
                if (!DateTime.TryParse(currentDateStr, out DateTime currentDate))
                    return null;

                DailyStatsData bestStats = null;
                DateTime bestDate = DateTime.MinValue;

                using (RegistryKey statsRoot = Registry.CurrentUser.OpenSubKey(@"Software\MonitorApp\Stats"))
                {
                    if (statsRoot != null)
                    {
                        foreach (var subKeyName in statsRoot.GetSubKeyNames())
                        {
                            if (DateTime.TryParse(subKeyName, out DateTime subDate))
                            {
                                if (subDate < currentDate.Date && subDate > bestDate)
                                {
                                    var stats = LoadDailyStatsForDate(subKeyName);
                                    if (stats != null)
                                    {
                                        bestDate = subDate;
                                        bestStats = stats;
                                    }
                                }
                            }
                        }
                    }
                }

                if (bestStats != null) return bestStats;

                // Fallback check root key
                using (RegistryKey rootKey = Registry.CurrentUser.OpenSubKey(@"Software\MonitorApp"))
                {
                    if (rootKey != null)
                    {
                        var dateVal = rootKey.GetValue("DailyStatsDate");
                        var statsVal = rootKey.GetValue("DailyStats");
                        if (dateVal != null && statsVal != null && DateTime.TryParse(dateVal.ToString(), out DateTime rootDate))
                        {
                            if (rootDate < currentDate.Date)
                            {
                                var loaded = JsonSerializer.Deserialize<DailyStatsData>(statsVal.ToString());
                                if (loaded != null)
                                {
                                    NormalizeStats(loaded);
                                    return loaded;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static void LoadDailyStatsFromRegistry()
        {
            string today = GetTrueBucharestTime().ToString("yyyy-MM-dd");
            var loaded = LoadDailyStatsForDate(today);
            if (loaded != null)
            {
                currentDailyStats = loaded;
                Console.WriteLine($"Loaded existing daily stats for {today}: Gaming={currentDailyStats.TotalGamingSeconds}s / {currentDailyStats.AvailableGamingSeconds}s");
            }
        }

        private static async Task SendDiscordChunkedMessageAsync(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage)) return;

            if (fullMessage.Length <= 1900)
            {
                var discordPayload = new { content = fullMessage };
                string discordJson = JsonSerializer.Serialize(discordPayload);
                var discordContent = new StringContent(discordJson, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(TextWebhookUrl, discordContent);
                SyncNetworkTimeFromResponse(response);
                return;
            }

            var lines = fullMessage.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            StringBuilder currentChunk = new StringBuilder();

            foreach (var line in lines)
            {
                if (currentChunk.Length + line.Length + 2 > 1900)
                {
                    if (currentChunk.Length > 0)
                    {
                        var discordPayload = new { content = currentChunk.ToString() };
                        string discordJson = JsonSerializer.Serialize(discordPayload);
                        var discordContent = new StringContent(discordJson, Encoding.UTF8, "application/json");
                        var response = await httpClient.PostAsync(TextWebhookUrl, discordContent);
                        SyncNetworkTimeFromResponse(response);
                        currentChunk.Clear();
                        await Task.Delay(250);
                    }
                }
                currentChunk.AppendLine(line);
            }

            if (currentChunk.Length > 0)
            {
                var discordPayload = new { content = currentChunk.ToString() };
                string discordJson = JsonSerializer.Serialize(discordPayload);
                var discordContent = new StringContent(discordJson, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(TextWebhookUrl, discordContent);
                SyncNetworkTimeFromResponse(response);
            }
        }

        private static async Task SendDailyReportAsync()
        {
            try
            {
                EnsureCurrentDayStats();
                if (currentDailyStats == null) return;

                int totalGamingSec = currentDailyStats.TotalGamingSeconds;
                int gamingMin = totalGamingSec / 60;
                int gamingRemSec = totalGamingSec % 60;

                int availableSec = currentDailyStats.AvailableGamingSeconds;
                int availableMin = availableSec / 60;

                int remainingSec = Math.Max(0, availableSec - totalGamingSec);
                int remainingMin = remainingSec / 60;
                int remainingRemSec = remainingSec % 60;

                int totalPcSec = currentDailyStats.TotalComputerSeconds;
                int pcHours = totalPcSec / 3600;
                int pcMinutes = (totalPcSec % 3600) / 60;
                int pcRemainingSec = totalPcSec % 60;

                int totalScreenSec = currentDailyStats.TotalScreenSeconds;
                int screenHours = totalScreenSec / 3600;
                int screenMinutes = (totalScreenSec % 3600) / 60;
                int screenRemainingSec = totalScreenSec % 60;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("📊 **Daily Activity Report**");
                sb.AppendLine($"- **Date:** `{currentDailyStats.Date}`");
                sb.AppendLine($"- **User:** `{currentUser}`");
                sb.AppendLine($"- **Time:** `{GetTrueBucharestTime():HH:mm}`");
                sb.AppendLine($"- **Allowed Time Window:** `{GetIntervalDisplayText()}`");
                sb.AppendLine($"- **Total PC On Time (Today):** `{pcHours}h {pcMinutes}m {pcRemainingSec}s` ({totalPcSec / 60} minutes)");
                if (maxScreenTimeMinutes > 0)
                {
                    int maxScreenSec = maxScreenTimeMinutes * 60;
                    int remScreenSec = Math.Max(0, maxScreenSec - totalScreenSec);
                    int remScreenMin = remScreenSec / 60;
                    int remScreenRemSec = remScreenSec % 60;
                    int screenPercent = (int)Math.Round((double)totalScreenSec / maxScreenSec * 100);
                    sb.AppendLine($"- **Active Screen Time (Today):** `{screenHours}h {screenMinutes}m {screenRemainingSec}s` / `{maxScreenTimeMinutes}m` ({screenPercent}%) - `{remScreenMin}m {remScreenRemSec}s` remaining");
                }
                else
                {
                    sb.AppendLine($"- **Active Screen Time (Today):** `{screenHours}h {screenMinutes}m {screenRemainingSec}s` ({totalScreenSec / 60} minutes)");
                }
                sb.AppendLine($"- **Current Mode:** {(isGamingModeActive ? "🟢 **Gaming Mode ACTIVE**" : "🔵 **School Mode ACTIVE**")}");

                // Gaming quota summary
                if (availableSec > 0)
                {
                    int percent = (int)Math.Round((double)totalGamingSec / availableSec * 100);
                    bool isOver = totalGamingSec >= availableSec;
                    string quotaStatus = isOver ? "🔴 **Expired (Forced School Mode)**" : $"🟢 **{remainingMin}m {remainingRemSec}s Remaining**";
                    sb.AppendLine($"- **Gaming Time Used:** `{gamingMin}m {gamingRemSec}s` / `{availableMin}m` ({percent}%)");
                    sb.AppendLine($"- **Available Bank (with carryover):** `{availableMin}m` (Daily base: `{dailyGameTimeMinutes}m`, Max carryover limit: `{5 * dailyGameTimeMinutes}m`)");
                    sb.AppendLine($"- **Gaming Quota Status:** {quotaStatus}");
                }
                else
                {
                    sb.AppendLine($"- **Gaming Time Recorded:** `{gamingMin}m {gamingRemSec}s` (No daily quota defined)");
                }
                sb.AppendLine();

                // Detailed breakdown of Games / Blocked Sites
                sb.AppendLine("**🎮 Gaming & Blocked Activity (Daily):**");
                var sortedGameStats = currentDailyStats.GameSeconds.OrderByDescending(x => x.Value).ToList();
                bool anyGame = false;
                foreach (var stat in sortedGameStats)
                {
                    if (stat.Value >= 5)
                    {
                        anyGame = true;
                        int m = stat.Value / 60;
                        int s = stat.Value % 60;
                        sb.AppendLine($"- **{stat.Key}**: {m}m {s}s ({stat.Value}s)");
                    }
                }
                if (!anyGame)
                {
                    sb.AppendLine("- No gaming activity recorded today");
                }
                sb.AppendLine();

                // Detailed breakdown of All Foreground Apps
                sb.AppendLine("**💻 Foreground Application Time (Daily):**");
                var sortedAppStats = currentDailyStats.AppSeconds.OrderByDescending(x => x.Value).ToList();
                bool anyApp = false;
                foreach (var stat in sortedAppStats)
                {
                    if (stat.Value >= 10)
                    {
                        anyApp = true;
                        int m = stat.Value / 60;
                        int s = stat.Value % 60;
                        sb.AppendLine($"- **{stat.Key}**: {m}m {s}s ({stat.Value}s)");
                    }
                }
                if (!anyApp)
                {
                    sb.AppendLine("- No application activity recorded today");
                }
                sb.AppendLine();

                // Detailed breakdown of Audio
                sb.AppendLine("**🔊 Audio Playback Time (Daily):**");
                var sortedAudioStats = currentDailyStats.AudioSeconds.OrderByDescending(x => x.Value).ToList();
                bool anyAudio = false;
                foreach (var stat in sortedAudioStats)
                {
                    if (stat.Value >= 10)
                    {
                        anyAudio = true;
                        int m = stat.Value / 60;
                        int s = stat.Value % 60;
                        sb.AppendLine($"- **{stat.Key}**: {m}m {s}s ({stat.Value}s)");
                    }
                }
                if (!anyAudio)
                {
                    sb.AppendLine("- No audio playback recorded today");
                }

                await SendDiscordChunkedMessageAsync(sb.ToString());
                Console.WriteLine("Discord daily stats report sent successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send daily report: {ex.Message}");
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
                        
                        // Deny PROCESS_TERMINATE (0x0001) and PROCESS_SUSPEND_RESUME (0x0800) to the current user
                        dacl.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x0001 | 0x0800, currentUserSid, false, null));
                        
                        byte[] newSd = new byte[dacl.BinaryLength];
                        dacl.GetBinaryForm(newSd, 0);
                        SetKernelObjectSecurity(hProcess, 4, newSd);
                        Console.WriteLine("Process termination and suspension protection applied.");
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

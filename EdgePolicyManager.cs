using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace Monitor
{
    /// <summary>
    /// Manages Microsoft Edge policies in the Windows Registry to enforce browser whitelisting,
    /// disable InPrivate (incognito) browsing, and prevent installing bypassing extensions.
    /// 
    /// =========================================================================================
    /// INTENTION & DESIGN NOTES (FOR FUTURE MAINTENANCE):
    /// =========================================================================================
    /// 1. Chromium-based browsers (including Microsoft Edge) check enterprise policies stored under:
    ///    - HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge
    ///    - HKEY_LOCAL_MACHINE\Software\Policies\Microsoft\Edge
    /// 
    /// 2. Policies Enforced:
    ///    - InPrivateModeAvailability = 1 (DWORD): Completely disables InPrivate browsing so
    ///      extensions and website filtering cannot be bypassed via incognito windows.
    ///    - ExtensionInstallBlocklist\1 = "*" (String): Disables extension installations,
    ///      preventing proxies, VPNs, or unapproved add-ons from being added.
    ///    - URLBlocklist\1 = "*" (String): In School Mode, blocks all websites by default.
    ///    - URLAllowlist\N = "url" (String): Allows only explicit educational / approved websites.
    ///    - RestoreOnStartup = 5 (DWORD): In School Mode, forces Edge to open a clean New Tab
    ///      page on startup, preventing pre-buffered sessions or blocked tabs from persisting.
    /// 
    /// 3. In Gaming Mode:
    ///    - URLBlocklist and URLAllowlist subkeys are removed, allowing free web browsing
    ///      during approved gaming sessions.
    ///    - RestoreOnStartup restriction is removed so user can use normal browser startup behavior.
    ///    - InPrivate and Extension restrictions remain enforced to maintain system security.
    /// 
    /// 4. Permissions & Elevation:
    ///    - By default, standard users may have read-only permissions on the Policies key.
    ///    - When Monitor.exe runs elevated or when the parent grants write permissions to the
    ///      HKCU\Software\Policies\Microsoft\Edge key, policy changes apply instantly without
    ///      requiring a browser restart.
    /// =========================================================================================
    /// </summary>
    public static class EdgePolicyManager
    {
        private const string EdgePolicySubKey = @"Software\Policies\Microsoft\Edge";
        private const string UrlBlocklistSubKey = @"Software\Policies\Microsoft\Edge\URLBlocklist";
        private const string UrlAllowlistSubKey = @"Software\Policies\Microsoft\Edge\URLAllowlist";
        private const string ExtensionBlocklistSubKey = @"Software\Policies\Microsoft\Edge\ExtensionInstallBlocklist";

        /// <summary>
        /// Applies School Mode restrictions:
        /// - URLBlocklist = ["*"] (blocks all websites)
        /// - URLAllowlist = [allowedWebsites] (allows only explicitly configured domains/URLs)
        /// - InPrivateModeAvailability = 1 (disables incognito)
        /// - ExtensionInstallBlocklist = ["*"] (blocks extension installation)
        /// - RestoreOnStartup = 5 (forces Edge to open clean New Tab page, preventing tab restores)
        /// </summary>
        public static void ApplySchoolModePolicies(IEnumerable<string> allowedWebsites)
        {
            try
            {
                // Enforce browser hardening (disable InPrivate and extension installation)
                ApplyBaseEdgeHardening();

                // Apply URLBlocklist = ["*"] to block all websites by default
                SetRegistryMultiValues(UrlBlocklistSubKey, new[] { "*" });

                // Apply URLAllowlist with permitted websites from configuration (normalized to Chromium URL pattern syntax)
                var siteList = allowedWebsites?
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(NormalizeUrlPattern)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                SetRegistryMultiValues(UrlAllowlistSubKey, siteList);

                // RestoreOnStartup: 5 = Open New Tab Page (cleans session restore so blocked tabs are not reloaded)
                SetRegistryDword(EdgePolicySubKey, "RestoreOnStartup", 5);

                Console.WriteLine($"[EdgePolicyManager] Applied School Mode: Edge website whitelist active ({siteList.Count} sites allowed, all others blocked).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EdgePolicyManager] Failed to apply School Mode Edge policies: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies Gaming Mode policies:
        /// - Removes URLBlocklist so user can access web freely during their gaming session.
        /// - Removes RestoreOnStartup policy so user's regular tab preference applies.
        /// - Keeps InPrivate and Extension blocking active for safety.
        /// </summary>
        public static void ApplyGamingModePolicies()
        {
            try
            {
                ApplyBaseEdgeHardening();

                // Remove website blocking so games / web platforms can load
                DeleteRegistrySubKeySafe(UrlBlocklistSubKey);
                DeleteRegistrySubKeySafe(UrlAllowlistSubKey);

                // Lift RestoreOnStartup restriction
                DeleteRegistryValueSafe(EdgePolicySubKey, "RestoreOnStartup");

                Console.WriteLine("[EdgePolicyManager] Applied Gaming Mode: Edge website restrictions lifted for gaming session.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EdgePolicyManager] Failed to lift Edge restrictions: {ex.Message}");
            }
        }

        /// <summary>
        /// Hardens Edge by disabling InPrivate mode and blocking extension installs.
        /// </summary>
        public static void ApplyBaseEdgeHardening()
        {
            // InPrivateModeAvailability: 0 = Enabled, 1 = Disabled, 2 = Forced
            SetRegistryDword(EdgePolicySubKey, "InPrivateModeAvailability", 1);

            // ExtensionInstallBlocklist: 1 = "*" (Block all extension installations)
            SetRegistryMultiValues(ExtensionBlocklistSubKey, new[] { "*" });

            // DeveloperToolsAvailability: 1 = Allow developer tools (F12, Inspect Element)
            SetRegistryDword(EdgePolicySubKey, "DeveloperToolsAvailability", 1);
        }

        /// <summary>
        /// Gracefully closes Microsoft Edge windows to immediately terminate any active media/video streams,
        /// WebSockets, and pre-buffered pages (e.g. YouTube). If any background process remains after a grace period,
        /// it terminates them.
        /// </summary>
        public static void CloseEdgeGracefully(int timeoutMs = 1500)
        {
            try
            {
                var edgeProcesses = Process.GetProcessesByName("msedge");
                if (edgeProcesses.Length == 0)
                {
                    return;
                }

                Console.WriteLine("[EdgePolicyManager] Closing Edge to terminate active streaming and preloaded pages...");

                // 1. Send WM_CLOSE to top-level windows for graceful closure
                foreach (var proc in edgeProcesses)
                {
                    try
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            proc.CloseMainWindow();
                        }
                    }
                    catch { }
                }

                // 2. Wait up to timeoutMs for processes to exit cleanly
                int elapsed = 0;
                int checkInterval = 100;
                while (elapsed < timeoutMs)
                {
                    Thread.Sleep(checkInterval);
                    elapsed += checkInterval;

                    if (Process.GetProcessesByName("msedge").Length == 0)
                    {
                        Console.WriteLine("[EdgePolicyManager] Edge closed cleanly.");
                        return;
                    }
                }

                // 3. Force terminate any lingering Edge processes (renderers, audio hosts, etc.)
                foreach (var proc in Process.GetProcessesByName("msedge"))
                {
                    try
                    {
                        proc.Kill(true);
                    }
                    catch { }
                }
                Console.WriteLine("[EdgePolicyManager] Terminated remaining Edge background processes.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EdgePolicyManager] Error during Edge closure: {ex.Message}");
            }
        }

        /// <summary>
        /// Completely clears all Edge policies managed by this application.
        /// </summary>
        public static void ClearAllPolicies()
        {
            DeleteRegistrySubKeySafe(UrlBlocklistSubKey);
            DeleteRegistrySubKeySafe(UrlAllowlistSubKey);
            DeleteRegistrySubKeySafe(ExtensionBlocklistSubKey);
            DeleteRegistryValueSafe(EdgePolicySubKey, "InPrivateModeAvailability");
            DeleteRegistryValueSafe(EdgePolicySubKey, "RestoreOnStartup");
            DeleteRegistryValueSafe(EdgePolicySubKey, "DeveloperToolsAvailability");
            Console.WriteLine("[EdgePolicyManager] Cleared all Edge policies.");
        }

        /// <summary>
        /// Normalizes user-configured website patterns to the standard Chromium URL pattern format
        /// expected by Microsoft Edge URLAllowlist / URLBlocklist policies:
        /// - Removes wildcard protocols like 'http*://' or 'https?://' (schemes don't support wildcards)
        /// - Strips trailing wildcards directly attached to hostnames (e.g. 'google.com*' -> 'google.com')
        /// - Strips invalid leading wildcards attached directly to words (e.g. '*pbinfo*' -> 'pbinfo.ro')
        /// - Normalizes '*.domain.com', '.domain.com', or bare domains to '[*.]domain.com' so Edge matches
        ///   both the apex domain and any subdomain (e.g. wikipedia.org and en.wikipedia.org).
        /// </summary>
        public static string NormalizeUrlPattern(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string p = raw.Trim();

            // 1. Strip wildcard / regex protocol prefixes (Chromium scheme must be exact or omitted)
            if (p.StartsWith("http*://", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(8);
            }
            else if (p.StartsWith("https*://", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(9);
            }
            else if (p.StartsWith("https?://", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(10);
            }

            // 2. Remove trailing asterisk if attached directly to domain name (e.g. "google.com*" -> "google.com")
            // Preserve if it's an explicit path wildcard (e.g. "domain.com/*")
            if (p.EndsWith("*") && !p.EndsWith("/*"))
            {
                p = p.TrimEnd('*');
            }

            // 3. Remove leading asterisk if directly attached to name without dot (e.g. "*nerdvana.ro" -> "nerdvana.ro")
            if (p.StartsWith("*") && !p.StartsWith("*."))
            {
                p = p.TrimStart('*');
            }

            // 4. Transform wildcards to official Chromium format: '[*.]domain.tld'
            if (p.StartsWith("*."))
            {
                p = "[*.]" + p.Substring(2);
            }
            else if (p.StartsWith("."))
            {
                p = "[*.]" + p.Substring(1);
            }
            else if (p.StartsWith("https://*.", StringComparison.OrdinalIgnoreCase))
            {
                p = "https://[*.]" + p.Substring(10);
            }
            else if (p.StartsWith("http://*.", StringComparison.OrdinalIgnoreCase))
            {
                p = "http://[*.]" + p.Substring(9);
            }
            else if (!p.Contains("://") && !p.StartsWith("[*.]") && !p.Contains("/"))
            {
                // Bare domain like "nerdarena.ro" or "pbinfo.ro" -> expand to '[*.]domain.ro'
                // so both the apex domain and all subdomains (e.g. www.) are permitted automatically
                if (p.Contains("."))
                {
                    p = "[*.]" + p;
                }
            }

            return p;
        }

        private static void SetRegistryDword(string subKeyPath, string valueName, int value)
        {
            TryWriteRegistry(Registry.CurrentUser, subKeyPath, key => key.SetValue(valueName, value, RegistryValueKind.DWord));
            TryWriteRegistry(Registry.LocalMachine, subKeyPath, key => key.SetValue(valueName, value, RegistryValueKind.DWord));
        }

        private static void SetRegistryMultiValues(string subKeyPath, IEnumerable<string> values)
        {
            Action<RegistryKey> writeAction = key =>
            {
                // Clear existing numbered keys first
                foreach (var vName in key.GetValueNames())
                {
                    key.DeleteValue(vName, false);
                }

                int index = 1;
                foreach (var val in values)
                {
                    key.SetValue(index.ToString(), val, RegistryValueKind.String);
                    index++;
                }
            };

            TryWriteRegistry(Registry.CurrentUser, subKeyPath, writeAction);
            TryWriteRegistry(Registry.LocalMachine, subKeyPath, writeAction);
        }

        private static void DeleteRegistrySubKeySafe(string subKeyPath)
        {
            TryDeleteSubKey(Registry.CurrentUser, subKeyPath);
            TryDeleteSubKey(Registry.LocalMachine, subKeyPath);
        }

        private static void DeleteRegistryValueSafe(string subKeyPath, string valueName)
        {
            TryDeleteValue(Registry.CurrentUser, subKeyPath, valueName);
            TryDeleteValue(Registry.LocalMachine, subKeyPath, valueName);
        }

        private static void TryWriteRegistry(RegistryKey root, string subKeyPath, Action<RegistryKey> action)
        {
            try
            {
                using (RegistryKey key = root.CreateSubKey(subKeyPath))
                {
                    if (key != null)
                    {
                        action(key);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Expected when running without Administrator privileges and root Policies key is restricted
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EdgePolicyManager] Notice ({root.Name}\\{subKeyPath}): {ex.Message}");
            }
        }

        private static void TryDeleteSubKey(RegistryKey root, string subKeyPath)
        {
            try
            {
                root.DeleteSubKeyTree(subKeyPath, false);
            }
            catch { }
        }

        private static void TryDeleteValue(RegistryKey root, string subKeyPath, string valueName)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKeyPath, true))
                {
                    key?.DeleteValue(valueName, false);
                }
            }
            catch { }
        }
    }
}

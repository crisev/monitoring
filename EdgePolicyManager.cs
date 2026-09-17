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
        private const string DeveloperToolsAllowlistSubKey = @"Software\Policies\Microsoft\Edge\DeveloperToolsAvailabilityAllowlist";
        private const string DeveloperToolsBlocklistSubKey = @"Software\Policies\Microsoft\Edge\DeveloperToolsAvailabilityBlocklist";

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

                // Apply URLBlocklist = ["*", "edge://surf"] to block all external websites by default and the built-in surf game
                SetRegistryMultiValues(UrlBlocklistSubKey, new[] { "*", "edge://surf" });

                // Internal browser schemes essential for Edge functionality:
                // - edge://* allows settings (edge://settings), history (edge://history / Ctrl-H), downloads (edge://downloads / Ctrl-J), favorites, new tab page, etc.
                // - devtools://* allows Developer Tools inspection window / panels (F12, Inspect Element)
                // - chrome://* allows internal compatibility redirects to edge:// pages
                var siteList = new List<string> { "edge://*", "devtools://*", "chrome://*" };

                if (allowedWebsites != null)
                {
                    siteList.AddRange(allowedWebsites
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(NormalizeUrlPattern)
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                }

                siteList = siteList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

                // Clear website blocking entries so user can browse freely during gaming sessions
                // We clear the values rather than deleting the subkeys to preserve subkey ACLs and permissions
                ClearRegistryValuesSafe(UrlBlocklistSubKey);
                ClearRegistryValuesSafe(UrlAllowlistSubKey);

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
        /// Hardens Edge by disabling InPrivate mode and blocking extension installs,
        /// while explicitly allowing Developer Tools (F12, Inspect Element) across all websites.
        /// </summary>
        public static void ApplyBaseEdgeHardening()
        {
            // InPrivateModeAvailability: 0 = Enabled, 1 = Disabled, 2 = Forced
            SetRegistryDword(EdgePolicySubKey, "InPrivateModeAvailability", 1);

            // ExtensionInstallBlocklist: 1 = "*" (Block all extension installations)
            SetRegistryMultiValues(ExtensionBlocklistSubKey, new[] { "*" });

            // DeveloperToolsAvailability: 1 = Allow developer tools (F12, Inspect Element)
            SetRegistryDword(EdgePolicySubKey, "DeveloperToolsAvailability", 1);

            // DeveloperToolsAvailabilityAllowlist: 1 = "*" (Explicitly permits DevTools on all URLs and frames, overriding default restrictions)
            SetRegistryMultiValues(DeveloperToolsAllowlistSubKey, new[] { "*" });

            // Ensure DeveloperToolsAvailabilityBlocklist is deleted
            DeleteRegistrySubKeySafe(DeveloperToolsBlocklistSubKey);
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
            DeleteRegistrySubKeySafe(DeveloperToolsAllowlistSubKey);
            DeleteRegistrySubKeySafe(DeveloperToolsBlocklistSubKey);
            DeleteRegistryValueSafe(EdgePolicySubKey, "InPrivateModeAvailability");
            DeleteRegistryValueSafe(EdgePolicySubKey, "RestoreOnStartup");
            DeleteRegistryValueSafe(EdgePolicySubKey, "DeveloperToolsAvailability");
            Console.WriteLine("[EdgePolicyManager] Cleared all Edge policies.");
        }

        /// <summary>
        /// Normalizes user-configured website patterns to the standard Chromium URL pattern format
        /// expected by Microsoft Edge URLAllowlist / URLBlocklist enterprise policies:
        /// Syntax: [scheme://][.]host[:port][/path][@query]
        /// - In Edge URLAllowlist, a bare hostname like 'nerdvana.ro' matches the apex domain AND all subdomains (e.g. education.nerdvana.ro).
        /// - Removes wildcard protocols like 'http*://' or 'https?://'
        /// - Strips '[*.]', '[*]', and leading '*.' prefixes (these belong to Chrome content settings, not URLAllowlist policy; Edge discards them as invalid hostname syntax)
        /// - Strips trailing wildcards directly attached to hostnames (e.g. 'google.com*' -> 'google.com')
        /// </summary>
        public static string NormalizeUrlPattern(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string p = raw.Trim();

            // Internal browser schemes should be preserved as-is (e.g. edge://*, devtools://*, chrome://*)
            if (p.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }

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

            // 2. Strip Chrome Content Settings syntax ('[*.]' or '[*]') from scheme or bare host
            // Microsoft Edge URLAllowlist / URLBlocklist enterprise policy does NOT support '[*.]'.
            // In URLAllowlist, specifying 'domain.com' automatically matches 'domain.com' AND all its subdomains!
            // Having '[*.]' causes Edge to reject the entry as a malformed hostname.
            if (p.StartsWith("https://[*.]", StringComparison.OrdinalIgnoreCase))
            {
                p = "https://" + p.Substring(12);
            }
            else if (p.StartsWith("http://[*.]", StringComparison.OrdinalIgnoreCase))
            {
                p = "http://" + p.Substring(11);
            }
            else if (p.StartsWith("https://*.", StringComparison.OrdinalIgnoreCase))
            {
                p = "https://" + p.Substring(10);
            }
            else if (p.StartsWith("http://*.", StringComparison.OrdinalIgnoreCase))
            {
                p = "http://" + p.Substring(9);
            }
            else if (p.StartsWith("[*.]", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(4);
            }
            else if (p.StartsWith("[*]", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(3);
            }
            else if (p.StartsWith("*.", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(2);
            }

            // 3. Remove trailing asterisk if attached directly to domain name (e.g. "google.com*" -> "google.com")
            // Preserve if it's an explicit path wildcard (e.g. "domain.com/*")
            if (p.EndsWith("*") && !p.EndsWith("/*"))
            {
                p = p.TrimEnd('*');
            }

            // 4. Remove leading asterisk if directly attached to name without dot (e.g. "*nerdvana.ro" -> "nerdvana.ro")
            if (p.StartsWith("*") && !p.StartsWith("*."))
            {
                p = p.TrimStart('*');
            }

            return p.Trim();
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

        private static void ClearRegistryValuesSafe(string subKeyPath)
        {
            Action<RegistryKey> clearAction = key =>
            {
                foreach (var vName in key.GetValueNames())
                {
                    key.DeleteValue(vName, false);
                }
            };

            TryWriteRegistry(Registry.CurrentUser, subKeyPath, clearAction);
            TryWriteRegistry(Registry.LocalMachine, subKeyPath, clearAction);
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
                // 1. Try opening existing key with write access first (avoids needing create permissions on parent)
                using (RegistryKey existingKey = root.OpenSubKey(subKeyPath, true))
                {
                    if (existingKey != null)
                    {
                        action(existingKey);
                        return;
                    }
                }

                // 2. If key doesn't exist yet, attempt to create it
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

        /// <summary>
        /// Ensures Edge policy registry keys exist under HKLM and HKCU, and if running with administrator
        /// privileges, grants FullControl permissions to BUILTIN\Users and Interactive User with inheritance.
        /// This allows the standard user monitoring session to dynamically manage allowlists/blocklists.
        /// </summary>
        public static void EnsurePolicyPermissions()
        {
            GrantKeyPermissions(Registry.LocalMachine, EdgePolicySubKey);
            GrantKeyPermissions(Registry.CurrentUser, EdgePolicySubKey);
        }

        private static void GrantKeyPermissions(RegistryKey root, string subKeyPath)
        {
            try
            {
                using (RegistryKey key = root.CreateSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree))
                {
                    if (key != null)
                    {
                        var acl = key.GetAccessControl();
                        var sidUsers = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
                        var ruleUsers = new System.Security.AccessControl.RegistryAccessRule(
                            sidUsers,
                            System.Security.AccessControl.RegistryRights.FullControl,
                            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                            System.Security.AccessControl.PropagationFlags.None,
                            System.Security.AccessControl.AccessControlType.Allow);
                        acl.AddAccessRule(ruleUsers);

                        var sidInteractive = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.InteractiveSid, null);
                        var ruleInteractive = new System.Security.AccessControl.RegistryAccessRule(
                            sidInteractive,
                            System.Security.AccessControl.RegistryRights.FullControl,
                            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                            System.Security.AccessControl.PropagationFlags.None,
                            System.Security.AccessControl.AccessControlType.Allow);
                        acl.AddAccessRule(ruleInteractive);

                        key.SetAccessControl(acl);
                    }
                }
            }
            catch { }
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

using Microsoft.Win32;

namespace Monitor.Core;

/// <summary>
/// Microsoft Edge policies, written by the service (SYSTEM) under HKLM only. HKLM keeps its default ACL
/// (Users: read), so the child cannot change them. Carried over from the original app's EdgePolicyManager.
/// <list type="bullet">
/// <item>Always: InPrivate off, no extension installs, DevTools allowed.</item>
/// <item>School mode: every site blocked except the allowlist (plus Edge's own pages); Edge opens a clean
/// New Tab page on start, so blocked tabs are not restored.</item>
/// <item>Gaming mode: no site restrictions.</item>
/// </list>
/// Edge watches these keys and applies changes within seconds, without a restart.
/// </summary>
public static class EdgePolicies
{
    public const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string UrlBlocklist = EdgeKey + @"\URLBlocklist";
    private const string UrlAllowlist = EdgeKey + @"\URLAllowlist";
    private const string ExtensionBlocklist = EdgeKey + @"\ExtensionInstallBlocklist";
    private const string DevToolsAllowlist = EdgeKey + @"\DeveloperToolsAvailabilityAllowlist";
    private const string DevToolsBlocklist = EdgeKey + @"\DeveloperToolsAvailabilityBlocklist";

    /// <summary>
    /// Blocked in School mode: everything not allowlisted, Edge's built-in surf game, and Google's embedded
    /// games (Snake, PAC-MAN, Solitaire, …), Doodle archive and interactive logos.
    /// </summary>
    public static readonly string[] SchoolBlocklist =
    [
        "*", "edge://surf", "*google.com/fbx*", "*google.com/doodles*", "*google.com/logos/*",
    ];

    /// <summary>Edge's own pages (settings, history, downloads), DevTools, and chrome:// redirects.</summary>
    public static readonly string[] BuiltInAllowlist = ["edge://*", "devtools://*", "chrome://*"];

    public static void ApplySchool(IEnumerable<string> allowedSites)
    {
        using var hklm = OpenHklm();
        ApplyHardening(hklm);
        SetList(hklm, UrlBlocklist, SchoolBlocklist);
        SetList(hklm, UrlAllowlist, SchoolAllowlist(allowedSites));
        using (var edge = hklm.CreateSubKey(EdgeKey, writable: true))
            edge.SetValue("RestoreOnStartup", 5, RegistryValueKind.DWord); // 5 = open the New Tab page
    }

    public static void ApplyGaming()
    {
        using var hklm = OpenHklm();
        ApplyHardening(hklm);
        hklm.DeleteSubKeyTree(UrlBlocklist, throwOnMissingSubKey: false);
        hklm.DeleteSubKeyTree(UrlAllowlist, throwOnMissingSubKey: false);
        using var edge = hklm.CreateSubKey(EdgeKey, writable: true);
        edge.DeleteValue("RestoreOnStartup", throwOnMissingValue: false);
    }

    /// <summary>Removes everything this app sets (admin logged on, or uninstall).</summary>
    public static void ClearAll()
    {
        using var hklm = OpenHklm();
        foreach (var key in new[] { UrlBlocklist, UrlAllowlist, ExtensionBlocklist, DevToolsAllowlist, DevToolsBlocklist })
            hklm.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        using var edge = hklm.OpenSubKey(EdgeKey, writable: true);
        if (edge is null) return;
        foreach (var name in new[] { "InPrivateModeAvailability", "RestoreOnStartup", "DeveloperToolsAvailability" })
            edge.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>
    /// Deletes Edge policies from a user's own hive (HKEY_USERS\&lt;SID&gt;). The original app wrote its lists
    /// there too and gave Users full control of that key; left behind, they would block sites in Gaming mode.
    /// Returns true if something was removed.
    /// </summary>
    public static bool RemoveUserPolicies(string userSid)
    {
        using var hive = Registry.Users.OpenSubKey(userSid, writable: true);
        if (hive is null) return false;
        using (var existing = hive.OpenSubKey(EdgeKey))
            if (existing is null) return false;
        hive.DeleteSubKeyTree(EdgeKey, throwOnMissingSubKey: false);
        return true;
    }

    /// <summary>The URLAllowlist for School mode: Edge's own pages plus the normalized parent list.</summary>
    public static List<string> SchoolAllowlist(IEnumerable<string> allowedSites) =>
        BuiltInAllowlist
            .Concat(allowedSites.Select(NormalizeUrlPattern))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Converts a site entry to the Chromium URL-filter format ([scheme://][.]host[:port][/path]).
    /// A bare host such as "pbinfo.ro" already matches all its subdomains, so wildcard prefixes
    /// ("*.", "[*.]", "http*://"), which Edge would reject, are removed.
    /// </summary>
    public static string NormalizeUrlPattern(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var p = raw.Trim();
        if (p.StartsWith("edge://", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase))
            return p;

        foreach (var prefix in new[] { "http*://", "https*://", "https?://" })
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { p = p[prefix.Length..]; break; }

        foreach (var (prefix, keep) in new[]
        {
            ("https://[*.]", "https://"), ("http://[*.]", "http://"), ("https://*.", "https://"), ("http://*.", "http://"),
            ("[*.]", ""), ("[*]", ""), ("*.", ""),
        })
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { p = keep + p[prefix.Length..]; break; }
        }

        if (p.EndsWith('*') && !p.EndsWith("/*")) p = p.TrimEnd('*');
        if (p.StartsWith('*')) p = p.TrimStart('*');
        return p.Trim();
    }

    private static RegistryKey OpenHklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static void ApplyHardening(RegistryKey hklm)
    {
        using (var edge = hklm.CreateSubKey(EdgeKey, writable: true))
        {
            edge.SetValue("InPrivateModeAvailability", 1, RegistryValueKind.DWord); // 1 = InPrivate disabled
            edge.SetValue("DeveloperToolsAvailability", 1, RegistryValueKind.DWord); // 1 = DevTools allowed
        }
        SetList(hklm, ExtensionBlocklist, ["*"]);
        SetList(hklm, DevToolsAllowlist, ["*"]);
        hklm.DeleteSubKeyTree(DevToolsBlocklist, throwOnMissingSubKey: false);
    }

    /// <summary>Writes a list policy as values "1", "2", … and removes any others.</summary>
    private static void SetList(RegistryKey hklm, string subKey, IEnumerable<string> values)
    {
        using var key = hklm.CreateSubKey(subKey, writable: true);
        var wanted = values.ToList();
        foreach (var name in key.GetValueNames())
            if (!int.TryParse(name, out var i) || i < 1 || i > wanted.Count) key.DeleteValue(name, throwOnMissingValue: false);
        for (int i = 0; i < wanted.Count; i++)
            key.SetValue((i + 1).ToString(), wanted[i], RegistryValueKind.String);
    }
}

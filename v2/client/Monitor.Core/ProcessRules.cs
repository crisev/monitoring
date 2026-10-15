namespace Monitor.Core;

/// <summary>One row of a process-tree snapshot.</summary>
public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name);

/// <summary>
/// Decides which processes may run in a child's session in School mode (whitelist only).
/// Carried over from the original app and cleaned up:
/// <list type="bullet">
/// <item>Paths are compared as folders, so <c>C:\WindowsGames\x.exe</c> is not mistaken for <c>C:\Windows</c>.</item>
/// <item>A program under <c>C:\Windows</c> is only trusted if its file is owned by the system (TrustedInstaller,
/// SYSTEM or Administrators). A standard user can write to a few folders there (e.g. <c>C:\Windows\Temp</c>),
/// and a file copied there is owned by that user.</item>
/// </list>
/// </summary>
public sealed class ProcessRules
{
    /// <summary>
    /// Windows processes that run in user sessions. Killing some of them (explorer, csrss, winlogon, dwm, userinit…)
    /// breaks the shell or crashes Windows. Console tools are allowed on purpose (decision 5 in the redesign plan).
    /// </summary>
    public static readonly IReadOnlySet<string> BaseWindowsProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Shell, taskbar, desktop
        "explorer", "sihost", "taskhostw", "ctfmon", "dwm", "conhost", "fontdrvhost", "csrss", "winlogon",
        "svchost", "dllhost", "unsecapp", "UserOOBEBroker",
        // Windows 10/11 UI
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI", "ShellExperienceHost", "ShellHost",
        "LockApp", "TextInputHost",
        // Security and app brokers
        "SecurityHealthSystray", "SecurityHealthHost", "DefenderSessionHelper", "smartscreen", "RuntimeBroker",
        "ApplicationFrameHost", "audiodg", "SystemSettings",
        // Runtimes used by Windows itself
        "msedgewebview2",
        // Logon (killing userinit makes the logon loop)
        "userinit", "rundll32",
        // Widgets and feeds (killing WidgetBoard restarts the taskbar)
        "WidgetBoard", "WidgetService", "MicrosoftStartFeedProvider", "FileCoAuth",
        // Sync and cross-device
        "mobsync", "SearchProtocolHost", "CrossDeviceService", "CrossDeviceResume", "PhoneExperienceHost",
        // Consoles and terminals
        "cmd", "powershell", "pwsh", "wt", "WindowsTerminal", "OpenConsole",
    };

    /// <summary>Script hosts and tools in C:\Windows that are not trusted just because of where they live.</summary>
    public static readonly IReadOnlySet<string> ForbiddenWindowsTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wscript", "cscript", "mshta", "regedit", "bash", "wsl",
    };

    /// <summary>Compilers, linkers and debuggers (MinGW, Code::Blocks, Clang), so programs can be built.</summary>
    public static readonly IReadOnlySet<string> ToolchainProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cb_console_runner", "cb_share_config", "gcc", "g++", "c++", "cc1", "cc1plus", "as", "ld", "collect2", "ar",
        "nm", "objdump", "ranlib", "strip", "windres", "gdb", "gdborig", "mingw32-make", "make", "clang", "clang++", "lld",
    };

    /// <summary>
    /// IDEs whose child processes may run, as long as they are console programs (the student's own compiled
    /// exercises). Windowed programs such as browsers and games are closed even when an IDE started them.
    /// </summary>
    public static readonly IReadOnlySet<string> IdeProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "codeblocks", "cb_console_runner", "gdb", "Code", "devenv",
    };

    /// <summary>
    /// Script and bytecode runtimes. They are console programs but can run anything, windowed games included
    /// (<c>java -jar</c>, Python with pygame), so being started from an IDE is not enough for them: they only
    /// run if the parent puts them on the allowed-apps list.
    /// </summary>
    public static readonly IReadOnlySet<string> ScriptRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "python", "pythonw", "py", "pyw", "java", "javaw", "node", "deno", "bun", "dotnet",
        "ruby", "rubyw", "perl", "php", "Rscript", "lua", "luajit",
    };

    /// <summary>Driver and hardware-utility folders under Program Files (only administrators can write there).</summary>
    public static readonly string[] HardwareVendorFolders =
    [
        "NVIDIA Corporation", "Realtek", "Intel", "ASUS", "Lenovo", "Dell", "HP", "Nahimic", "Synaptics", "ELAN",
    ];

    /// <summary>
    /// Edge's own folders (helpers such as identity_helper, the updater, WebView2). Edge itself (msedge)
    /// is still governed by the allowed-apps list.
    /// </summary>
    public static readonly string[] EdgeFolders =
    [
        @"Microsoft\Edge", @"Microsoft\EdgeCore", @"Microsoft\EdgeUpdate", @"Microsoft\EdgeWebView",
    ];

    /// <summary>Windows shell packages in Program Files\WindowsApps.</summary>
    public static readonly string[] WindowsAppsSystemPackages =
    [
        "MicrosoftWindows.Client.", "Microsoft.Widgets", "Microsoft.StartExperiencesApp", "MicrosoftWindows.CrossDevice",
    ];

    /// <summary>How many generations up to look for an IDE.</summary>
    public const int IdeAncestorDepth = 4;

    private readonly HashSet<string> allowedApps;
    private readonly string windowsDir;
    private readonly string[] trustedFolders;
    private readonly string[] edgeFolders;
    private readonly string windowsAppsDir;
    private readonly Func<string, bool> isSystemOwned;
    private readonly Func<string, bool> isConsoleProgram;

    /// <param name="allowedApps">Executable names from the parent's settings.</param>
    /// <param name="installDir">The Monitor v2 install folder (the agent runs from there).</param>
    /// <param name="isSystemOwned">Whether a file under C:\Windows is owned by the system (see <see cref="TrustedFiles"/>).</param>
    /// <param name="isConsoleProgram">Whether an executable is a console program (default: <see cref="ExecutableImage.IsConsoleProgram"/>).</param>
    public ProcessRules(
        IEnumerable<string> allowedApps,
        string installDir,
        Func<string, bool> isSystemOwned,
        string? windowsDir = null,
        string? programFiles = null,
        string? programFilesX86 = null,
        Func<string, bool>? isConsoleProgram = null)
    {
        this.allowedApps = new HashSet<string>(allowedApps.Select(NormalizeName).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        this.isSystemOwned = isSystemOwned;
        this.isConsoleProgram = isConsoleProgram ?? ExecutableImage.IsConsoleProgram;
        this.windowsDir = AsFolder(windowsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var pf = programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = programFilesX86 ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        windowsAppsDir = AsFolder(JoinPath(pf, "WindowsApps"));
        var folders = new List<string> { AsFolder(installDir) };
        foreach (var vendor in HardwareVendorFolders)
        {
            folders.Add(AsFolder(JoinPath(pf, vendor)));
            if (!string.IsNullOrEmpty(pf86)) folders.Add(AsFolder(JoinPath(pf86, vendor)));
        }
        trustedFolders = [.. folders];
        edgeFolders = [.. EdgeFolders.SelectMany(f => new[] { pf, pf86 }.Where(d => !string.IsNullOrEmpty(d)).Select(d => AsFolder(JoinPath(d!, f))))];
    }

    /// <summary>"Code.exe " → "Code".</summary>
    public static string NormalizeName(string? name)
    {
        var n = (name ?? "").Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>Why the process may keep running in School mode, or null if it must be closed.</summary>
    /// <param name="tree">Snapshot of all processes, by PID (used for the IDE rule).</param>
    public string? AllowReason(int pid, string name, string? path, IReadOnlyDictionary<int, ProcessEntry> tree)
    {
        name = NormalizeName(name);
        if (BaseWindowsProcesses.Contains(name)) return "windows";
        if (allowedApps.Contains(name)) return "allowed";
        if (IsTrustedPath(name, path)) return "trusted-path";
        if (ToolchainProcesses.Contains(name)) return "toolchain";
        if (IsStudentProgram(pid, name, path, tree)) return "ide-child";
        return null;
    }

    /// <summary>
    /// A console program started from an IDE (up to <see cref="IdeAncestorDepth"/> levels down): what Code::Blocks
    /// and VS Code produce for the student's exercises. Each process is judged by its own executable, so a
    /// console program that launches a browser does not make the browser allowed. Script runtimes and
    /// programs whose file can't be read are not covered.
    /// </summary>
    private bool IsStudentProgram(int pid, string name, string? path, IReadOnlyDictionary<int, ProcessEntry> tree) =>
        !string.IsNullOrEmpty(path)
        && !ScriptRuntimes.Contains(name)
        && IsSpawnedByIde(pid, tree)
        && isConsoleProgram(path);

    public bool IsTrustedPath(string name, string? path)
    {
        if (string.IsNullOrEmpty(path)) return false; // unknown binary (e.g. anti-cheat protected): not trusted
        if (path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
            return !ForbiddenWindowsTools.Contains(NormalizeName(name)) && isSystemOwned(path);
        foreach (var folder in trustedFolders)
            if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
        if (!NormalizeName(name).Equals("msedge", StringComparison.OrdinalIgnoreCase))
            foreach (var folder in edgeFolders)
                if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(windowsAppsDir, StringComparison.OrdinalIgnoreCase))
        {
            var package = path[windowsAppsDir.Length..];
            return WindowsAppsSystemPackages.Any(p => package.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    public static bool IsSpawnedByIde(int pid, IReadOnlyDictionary<int, ProcessEntry> tree)
    {
        int current = pid;
        for (int depth = 0; depth < IdeAncestorDepth; depth++)
        {
            if (!tree.TryGetValue(current, out var entry) || entry.ParentPid <= 0 || entry.ParentPid == current) return false;
            if (!tree.TryGetValue(entry.ParentPid, out var parent)) return false;
            if (IdeProcesses.Contains(NormalizeName(parent.Name))) return true;
            current = parent.Pid;
        }
        return false;
    }

    /// <summary>The blocked-title fragment contained in <paramref name="title"/>, if any.</summary>
    public static string? MatchBlockedTitle(string? title, IEnumerable<string> blockedTitles)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        foreach (var fragment in blockedTitles)
            if (!string.IsNullOrWhiteSpace(fragment) && title.Contains(fragment.Trim(), StringComparison.OrdinalIgnoreCase))
                return fragment;
        return null;
    }

    // Windows paths are built with '\' explicitly (not Path.Combine), so the rules behave the same when the
    // unit tests run on another OS.
    private static string JoinPath(string folder, string child) => folder.TrimEnd('\\', '/') + '\\' + child;

    private static string AsFolder(string path) => path.TrimEnd('\\', '/') + '\\';
}

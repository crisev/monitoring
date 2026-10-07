using Monitor.Core;

namespace Monitor.Core.Tests;

public class ProcessRulesTests
{
    private const string Windows = @"C:\Windows";
    private const string Pf = @"C:\Program Files";
    private const string Pf86 = @"C:\Program Files (x86)";
    private const string Install = @"C:\Program Files\MonitorV2";

    private static readonly Dictionary<int, ProcessEntry> NoTree = [];

    private static ProcessRules Rules(Func<string, bool>? owned = null, params string[] allowed) =>
        new(allowed.Length > 0 ? allowed : ["msedge", "Code.exe", " notepad "], Install, owned ?? (_ => true), Windows, Pf, Pf86);

    [Theory]
    [InlineData("explorer")]
    [InlineData("explorer.exe")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("WindowsTerminal")]
    public void Windows_shell_and_consoles_are_allowed(string name) =>
        Assert.Equal("windows", Rules().AllowReason(1, name, null, NoTree));

    [Theory]
    [InlineData("msedge")]
    [InlineData("code")]
    [InlineData("Code.exe")]
    [InlineData("notepad")]
    public void Parent_allowed_apps_match_without_case_or_extension(string name) =>
        Assert.Equal("allowed", Rules().AllowReason(1, name, null, NoTree));

    [Theory]
    [InlineData("RobloxPlayerBeta", @"C:\Users\kid\AppData\Local\Roblox\Versions\x\RobloxPlayerBeta.exe")]
    [InlineData("game", null)]
    [InlineData("game", @"C:\WindowsGames\game.exe")] // not inside C:\Windows
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    public void Other_programs_are_not_allowed(string name, string? path) =>
        Assert.Null(Rules().AllowReason(1, name, path, NoTree));

    [Fact]
    public void Programs_in_Windows_are_trusted_only_when_owned_by_the_system()
    {
        var path = @"C:\Windows\System32\taskmgr.exe";
        Assert.Equal("trusted-path", Rules(_ => true).AllowReason(1, "taskmgr", path, NoTree));
        // e.g. a game copied into C:\Windows\Temp, which standard users can write to
        Assert.Null(Rules(_ => false).AllowReason(1, "game", @"C:\Windows\Temp\game.exe", NoTree));
    }

    [Theory]
    [InlineData("mshta")]
    [InlineData("wscript")]
    [InlineData("regedit")]
    public void Script_hosts_in_Windows_are_not_trusted(string name) =>
        Assert.Null(Rules(_ => true).AllowReason(1, name, $@"C:\Windows\System32\{name}.exe", NoTree));

    [Theory]
    [InlineData(@"C:\Program Files\Realtek\Audio\HDA\RtkAudUService64.exe")]
    [InlineData(@"C:\Program Files (x86)\Intel\Graphics\igfx.exe")]
    [InlineData(@"C:\Program Files\MonitorV2\Monitor.Agent.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\MicrosoftWindows.Client.WebExperience_1.0\Widgets.exe")]
    public void Driver_install_and_shell_package_folders_are_trusted(string path) =>
        Assert.Equal("trusted-path", Rules().AllowReason(1, Path.GetFileNameWithoutExtension(path), path, NoTree));

    [Fact]
    public void Edge_helpers_are_trusted_but_Edge_itself_follows_the_allowed_list()
    {
        var helper = @"C:\Program Files (x86)\Microsoft\Edge\Application\154.0.4258.62\identity_helper.exe";
        Assert.Equal("trusted-path", Rules().AllowReason(1, "identity_helper", helper, NoTree));
        var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        Assert.Null(Rules(allowed: "notepad").AllowReason(1, "msedge", edge, NoTree));
        Assert.Null(Rules().AllowReason(1, "game", @"C:\Program Files (x86)\Microsoft\EdgeGames\game.exe", NoTree));
    }

    [Fact]
    public void Store_games_are_not_trusted() =>
        Assert.Null(Rules().AllowReason(1, "Minecraft", @"C:\Program Files\WindowsApps\Microsoft.MinecraftUWP_1.0\Minecraft.Windows.exe", NoTree));

    [Fact]
    public void Toolchain_is_allowed() =>
        Assert.Equal("toolchain", Rules().AllowReason(1, "g++", @"C:\MinGW\bin\g++.exe", NoTree));

    [Fact]
    public void Programs_started_from_an_IDE_are_allowed_up_to_four_levels()
    {
        var tree = new Dictionary<int, ProcessEntry>
        {
            [10] = new(10, 1, "codeblocks"),
            [11] = new(11, 10, "cb_console_runner"),
            [12] = new(12, 11, "main"),
            [20] = new(20, 1, "explorer"),
            [21] = new(21, 20, "a"),
            [22] = new(22, 21, "b"),
            [23] = new(23, 22, "c"),
            [24] = new(24, 23, "d"),
            [30] = new(30, 10, "x1"),
            [31] = new(31, 30, "x2"),
            [32] = new(32, 31, "x3"),
            [33] = new(33, 32, "x4"),
            [34] = new(34, 33, "x5"),
        };
        Assert.Equal("ide-child", Rules().AllowReason(12, "main", @"C:\Users\kid\teme\main.exe", tree));
        Assert.Null(Rules().AllowReason(24, "d", @"C:\Users\kid\d.exe", tree));
        Assert.Equal("ide-child", Rules().AllowReason(33, "x4", null, tree));
        Assert.Null(Rules().AllowReason(34, "x5", null, tree));
    }

    [Fact]
    public void Blocked_titles_match_case_insensitively()
    {
        string[] blocked = ["YouTube", " Play Snake ", ""];
        Assert.Equal("YouTube", ProcessRules.MatchBlockedTitle("Minecraft but… - youtube", blocked));
        Assert.Equal(" Play Snake ", ProcessRules.MatchBlockedTitle("Play Snake - Google Search", blocked));
        Assert.Null(ProcessRules.MatchBlockedTitle("Problema #1324 - pbinfo.ro", blocked));
        Assert.Null(ProcessRules.MatchBlockedTitle(null, blocked));
    }
}

public class EdgePoliciesTests
{
    [Theory]
    [InlineData("pbinfo.ro", "pbinfo.ro")]
    [InlineData("  *.wikipedia.org ", "wikipedia.org")]
    [InlineData("[*.]google.com", "google.com")]
    [InlineData("https://*.khanacademy.org", "https://khanacademy.org")]
    [InlineData("http*://codeforces.com", "codeforces.com")]
    [InlineData("https?://kilonova.ro", "kilonova.ro")]
    [InlineData("google.com*", "google.com")]
    [InlineData("example.com/docs/*", "example.com/docs/*")]
    [InlineData("*nerdvana.ro", "nerdvana.ro")]
    [InlineData("edge://settings", "edge://settings")]
    [InlineData("", "")]
    public void Normalizes_site_patterns(string raw, string expected) =>
        Assert.Equal(expected, EdgePolicies.NormalizeUrlPattern(raw));

    [Fact]
    public void School_allowlist_keeps_Edge_pages_and_removes_duplicates()
    {
        var list = EdgePolicies.SchoolAllowlist(["pbinfo.ro", "*.pbinfo.ro", "", "  "]);
        Assert.Equal(["edge://*", "devtools://*", "chrome://*", "pbinfo.ro"], list);
    }
}

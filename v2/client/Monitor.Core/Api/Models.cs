namespace Monitor.Core.Api;

// Shapes of the device API (see v2/README.md, "Device API"). JSON uses camelCase.

/// <summary>What the PC must enforce. Returned by heartbeat, game/start and game/stop.</summary>
public sealed class DeviceState
{
    public long ServerTime { get; set; }
    public string Day { get; set; } = "";
    public string DeviceId { get; set; } = "";
    /// <summary>"gaming" or "school".</summary>
    public string Mode { get; set; } = "school";
    public GameSession? Session { get; set; }
    public GameStatus Game { get; set; } = new();
    public ScreenStatus Screen { get; set; } = new();
    public int ConfigVersion { get; set; }
    public Timing Timing { get; set; } = new();

    public bool IsGaming => Mode == "gaming" && Session is not null;
}

public sealed class GameSession
{
    public string Id { get; set; } = "";
    public int LeaseRemainingSeconds { get; set; }
}

public sealed class GameStatus
{
    public int BalanceSeconds { get; set; }
    public int UsedTodaySeconds { get; set; }
}

public sealed class ScreenStatus
{
    /// <summary>null = no daily limit.</summary>
    public int? LimitSeconds { get; set; }
    public int UsedSeconds { get; set; }
    /// <summary>null = no daily limit.</summary>
    public int? RemainingSeconds { get; set; }
    public int ExtraSeconds { get; set; }
}

public sealed class Timing
{
    public int HeartbeatSeconds { get; set; } = 30;
    public int LeaseSeconds { get; set; } = 90;
    public int OfflineBudgetSeconds { get; set; } = 3600;
}

/// <summary>School-mode configuration (GET /api/device/config).</summary>
public sealed class DeviceConfig
{
    public int Version { get; set; }
    public string TimeZone { get; set; } = "Europe/Bucharest";
    public List<string> AllowedApps { get; set; } = [.. Defaults.AllowedApps];
    public List<string> AllowedSites { get; set; } = [.. Defaults.AllowedSites];
    public List<string> BlockedTitles { get; set; } = [.. Defaults.BlockedTitles];
    public int ScreenshotIntervalMinutes { get; set; } = 5;
    public int OfflineBudgetMinutes { get; set; } = 60;
}

public sealed class HeartbeatRequest
{
    public string? SessionId { get; set; }
    public bool ScreenActive { get; set; }
    public int ScreenSeconds { get; set; }
    public string? ClientVersion { get; set; }
}

public sealed class EnrollResponse
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Token { get; set; } = "";
}

public sealed class ActivityItem
{
    /// <summary>When the stretch started, in seconds before sending (PC monotonic clock).</summary>
    public int SecondsAgo { get; set; }
    public string Mode { get; set; } = "school";
    public string App { get; set; } = "";
    public string? Site { get; set; }
    public string? Title { get; set; }
    public int Seconds { get; set; }
    public int AudioSeconds { get; set; }
    public int Blocked { get; set; }
}

public sealed class DeviceEvent
{
    public string Type { get; set; } = "";
    public object? Detail { get; set; }
}

/// <summary>Built-in School-mode lists, used until the server's config has been fetched once. Same as the server defaults.</summary>
public static class Defaults
{
    public static readonly string[] AllowedApps =
    [
        "msedge", "WINWORD", "Code", "codeblocks", "notepad", "CalculatorApp",
        "cmd", "powershell", "pwsh", "WindowsTerminal", "wt",
    ];

    public static readonly string[] AllowedSites =
    [
        "wikipedia.org", "nerdvana.ro", "pbinfo.ro", "nerdarena.ro", "infoarena.ro",
        "codeforces.com", "kilonova.ro", "google.com",
    ];

    public static readonly string[] BlockedTitles =
    [
        "YouTube", "Agar.io", "diep.io", "Twitch", "Poki", "Infinite Craft", "Play Snake", "Play PAC-MAN",
        "Google Doodles", "Play Solitaire", "Play Minesweeper", "Play Tic-tac-toe",
    ];
}

using Monitor.Core.Api;

namespace Monitor.Core;

/// <summary>The part of <see cref="ModeClock"/> that survives a restart (kept in the service's state file).</summary>
public sealed class ClockSnapshot
{
    /// <summary>Screen-in-use seconds measured but not yet acknowledged by the server.</summary>
    public double PendingScreenSeconds { get; set; }
    /// <summary>Screen time left as of the last server reply; null = no limit (or never heard from the server).</summary>
    public int? ServerScreenRemainingSeconds { get; set; }
    /// <summary>Screen-in-use seconds since the last successful server contact.</summary>
    public double OfflineUsedSeconds { get; set; }
    public int OfflineBudgetSeconds { get; set; } = 3600;
    public int GameBalanceSeconds { get; set; }
    public int GameUsedTodaySeconds { get; set; }
}

public enum ReplyKind { Heartbeat, Start, Stop }

/// <summary>
/// The PC's local copy of the server's decisions, counted down on a monotonic clock
/// (<see cref="Environment.TickCount64"/>, in milliseconds). The wall clock is never used.
/// <list type="bullet">
/// <item>Game lease: gaming lasts until the deadline of the last lease the server granted.</item>
/// <item>Screen time: seconds in use are counted locally until the server acknowledges them.</item>
/// <item>Offline budget: screen time used since the last successful server contact.</item>
/// </list>
/// Not thread-safe: the caller serializes access.
/// </summary>
public sealed class ModeClock
{
    /// <summary>
    /// A longer gap between two ticks (sleep, hibernation, a stalled process) counts only this much,
    /// so the time a PC spent asleep is never counted as screen time.
    /// </summary>
    public const long MaxTickGapMs = 5_000;

    private long lastTick;
    private long leaseDeadline;
    private double pendingScreenMs;
    private int? serverScreenRemaining;
    private double offlineUsedMs;
    private int balanceAtReply;
    private int usedTodayAtReply;
    private double gamingSinceReplyMs;

    public ModeClock(long now, ClockSnapshot? saved = null)
    {
        lastTick = now;
        if (saved is null) return;
        pendingScreenMs = Math.Max(0, saved.PendingScreenSeconds) * 1000;
        serverScreenRemaining = saved.ServerScreenRemainingSeconds;
        offlineUsedMs = Math.Max(0, saved.OfflineUsedSeconds) * 1000;
        OfflineBudgetSeconds = saved.OfflineBudgetSeconds;
        balanceAtReply = saved.GameBalanceSeconds;
        usedTodayAtReply = saved.GameUsedTodaySeconds;
    }

    /// <summary>The game session being played, or null in School mode.</summary>
    public string? SessionId { get; private set; }

    /// <summary>True after a successful server call, false after a failed one (and at start).</summary>
    public bool Online { get; private set; }

    public int OfflineBudgetSeconds { get; private set; } = 3600;

    public DeviceState? LastState { get; private set; }

    public bool IsGaming(long now) => SessionId is not null && now < leaseDeadline;

    public int LeaseRemainingSeconds(long now) =>
        IsGaming(now) ? (int)Math.Ceiling((leaseDeadline - now) / 1000.0) : 0;

    /// <summary>Screen time left today; null = no limit.</summary>
    public int? ScreenRemainingSeconds =>
        serverScreenRemaining is int r ? Math.Max(0, r - (int)(pendingScreenMs / 1000)) : null;

    /// <summary>Seconds to send as <c>screenSeconds</c> in the next heartbeat.</summary>
    public int ScreenSecondsToReport => (int)Math.Min(86_400, Math.Floor(pendingScreenMs / 1000));

    public int OfflineRemainingSeconds => Math.Max(0, OfflineBudgetSeconds - (int)(offlineUsedMs / 1000));

    public bool OfflineBudgetExhausted => !Online && offlineUsedMs >= OfflineBudgetSeconds * 1000.0;

    /// <summary>Game balance, counted down locally while a session runs.</summary>
    public int GameBalanceSeconds => Math.Max(0, balanceAtReply - (int)(gamingSinceReplyMs / 1000));

    public int GameUsedTodaySeconds => usedTodayAtReply + (int)(gamingSinceReplyMs / 1000);

    /// <summary>Advances the clock. Returns true if the game lease ran out during this tick.</summary>
    public bool Tick(long now, bool screenActive)
    {
        long delta = Math.Clamp(now - lastTick, 0, MaxTickGapMs);
        lastTick = now;
        if (screenActive)
        {
            pendingScreenMs += delta;
            if (!Online) offlineUsedMs += delta;
        }
        if (SessionId is null) return false;
        if (now < leaseDeadline)
        {
            gamingSinceReplyMs += delta;
            return false;
        }
        SessionId = null;
        return true;
    }

    /// <summary>Ends gaming locally right away (GAME OFF, or the agent went missing); the server is told separately.</summary>
    public void EndGaming() => SessionId = null;

    public void MarkOffline() => Online = false;

    /// <summary>
    /// Applies a server reply. <paramref name="requestStart"/> is the tick when the request was sent, so the
    /// local lease deadline can only be earlier than the server's, never later.
    /// Only a game/start reply can switch School mode into Gaming; a heartbeat can keep or end the current session.
    /// </summary>
    public void Apply(DeviceState state, long requestStart, ReplyKind kind, int reportedScreenSeconds = 0, string? sentSessionId = null)
    {
        pendingScreenMs = Math.Max(0, pendingScreenMs - reportedScreenSeconds * 1000.0);
        serverScreenRemaining = state.Screen.RemainingSeconds;
        Online = true;
        offlineUsedMs = 0;
        OfflineBudgetSeconds = state.Timing.OfflineBudgetSeconds;
        LastState = state;
        balanceAtReply = state.Game.BalanceSeconds;
        usedTodayAtReply = state.Game.UsedTodaySeconds;
        gamingSinceReplyMs = 0;

        if (state.IsGaming)
        {
            var id = state.Session!.Id;
            bool keep = kind == ReplyKind.Start
                || (kind == ReplyKind.Heartbeat && sentSessionId == id && SessionId == id);
            if (keep)
            {
                SessionId = id;
                leaseDeadline = requestStart + state.Session.LeaseRemainingSeconds * 1000L;
            }
        }
        else
        {
            SessionId = null;
        }
    }

    public ClockSnapshot Snapshot() => new()
    {
        PendingScreenSeconds = pendingScreenMs / 1000,
        ServerScreenRemainingSeconds = serverScreenRemaining,
        OfflineUsedSeconds = offlineUsedMs / 1000,
        OfflineBudgetSeconds = OfflineBudgetSeconds,
        GameBalanceSeconds = GameBalanceSeconds,
        GameUsedTodaySeconds = GameUsedTodaySeconds,
    };
}

/// <summary>Fires each "N minutes left" warning once, and again only after the time left goes back above it.</summary>
public sealed class CountdownWarnings
{
    private readonly int[] thresholds;
    private readonly bool[] fired;

    public CountdownWarnings(params int[] thresholdsSeconds)
    {
        thresholds = [.. thresholdsSeconds.OrderDescending()];
        fired = new bool[thresholds.Length];
    }

    /// <summary>Returns the threshold to announce now (the smallest one just reached), or null.</summary>
    public int? Check(int? remainingSeconds)
    {
        if (remainingSeconds is not int remaining)
        {
            Reset();
            return null;
        }
        int? announce = null;
        for (int i = 0; i < thresholds.Length; i++)
        {
            if (remaining > thresholds[i]) fired[i] = false;
            else if (!fired[i])
            {
                fired[i] = true;
                announce = thresholds[i];
            }
        }
        return announce;
    }

    public void Reset() => Array.Clear(fired);
}

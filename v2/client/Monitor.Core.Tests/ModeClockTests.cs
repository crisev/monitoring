using Monitor.Core;
using Monitor.Core.Api;

namespace Monitor.Core.Tests;

public class ModeClockTests
{
    private static DeviceState State(string? sessionId = null, int lease = 0, int? screenRemaining = 3600,
        int balance = 600, int offlineBudget = 3600) => new()
    {
        Mode = sessionId is null ? "school" : "gaming",
        Session = sessionId is null ? null : new GameSession { Id = sessionId, LeaseRemainingSeconds = lease },
        Game = new GameStatus { BalanceSeconds = balance },
        Screen = new ScreenStatus { RemainingSeconds = screenRemaining, LimitSeconds = screenRemaining is null ? null : 10_800 },
        Timing = new Timing { OfflineBudgetSeconds = offlineBudget },
    };

    [Fact]
    public void Start_reply_enters_gaming_until_the_lease_runs_out()
    {
        var c = new ModeClock(0);
        c.Apply(State("s1", lease: 90), requestStart: 1_000, ReplyKind.Start);

        Assert.True(c.IsGaming(50_000));
        Assert.Equal(41, c.LeaseRemainingSeconds(50_000));
        Assert.False(c.Tick(90_999, screenActive: true));
        Assert.True(c.Tick(91_000, screenActive: true)); // deadline = request start + lease, never later
        Assert.Null(c.SessionId);
        Assert.False(c.IsGaming(91_000));
    }

    [Fact]
    public void Heartbeat_renews_the_current_session_from_the_request_time()
    {
        var c = new ModeClock(0);
        c.Apply(State("s1", lease: 90), 0, ReplyKind.Start);
        c.Apply(State("s1", lease: 90), requestStart: 30_000, ReplyKind.Heartbeat, sentSessionId: "s1");

        Assert.True(c.IsGaming(119_999));
        Assert.False(c.IsGaming(120_000));
    }

    [Fact]
    public void Heartbeat_never_switches_school_into_gaming()
    {
        var c = new ModeClock(0);
        c.Apply(State("s1", lease: 90), 0, ReplyKind.Start);
        c.EndGaming(); // GAME OFF pressed while a heartbeat was in flight
        c.Apply(State("s1", lease: 90), 10_000, ReplyKind.Heartbeat, sentSessionId: "s1");
        Assert.False(c.IsGaming(20_000));

        var fresh = new ModeClock(0);
        fresh.Apply(State("stray", lease: 90), 0, ReplyKind.Heartbeat, sentSessionId: null);
        Assert.False(fresh.IsGaming(1_000));
    }

    [Fact]
    public void Heartbeat_in_school_mode_ends_gaming()
    {
        var c = new ModeClock(0);
        c.Apply(State("s1", lease: 90), 0, ReplyKind.Start);
        c.Apply(State(), 30_000, ReplyKind.Heartbeat, sentSessionId: "s1"); // "End gaming now" in the web app
        Assert.False(c.IsGaming(31_000));
        Assert.Null(c.SessionId);
    }

    [Fact]
    public void Screen_time_counts_down_locally_until_acknowledged()
    {
        var c = new ModeClock(0);
        c.Apply(State(screenRemaining: 600), 0, ReplyKind.Heartbeat);
        for (long t = 1_000; t <= 100_000; t += 1_000) c.Tick(t, screenActive: true);

        Assert.Equal(100, c.ScreenSecondsToReport);
        Assert.Equal(500, c.ScreenRemainingSeconds);

        // The server acknowledged the 100 s and says 500 s are left; seconds counted meanwhile still count.
        c.Tick(103_000, screenActive: true);
        c.Apply(State(screenRemaining: 500), 100_000, ReplyKind.Heartbeat, reportedScreenSeconds: 100);
        Assert.Equal(3, c.ScreenSecondsToReport);
        Assert.Equal(497, c.ScreenRemainingSeconds);
    }

    [Fact]
    public void Locked_screen_and_sleep_do_not_count()
    {
        var c = new ModeClock(0);
        c.Apply(State(screenRemaining: 600), 0, ReplyKind.Heartbeat);
        c.Tick(10_000, screenActive: false);
        Assert.Equal(0, c.ScreenSecondsToReport);

        c.Tick(11_000, screenActive: true);
        c.Tick(3_611_000, screenActive: true); // one hour asleep: counts as at most one gap
        Assert.Equal(1 + ModeClock.MaxTickGapMs / 1000, c.ScreenSecondsToReport);
    }

    [Fact]
    public void No_limit_means_no_remaining_value()
    {
        var c = new ModeClock(0);
        c.Apply(State(screenRemaining: null), 0, ReplyKind.Heartbeat);
        c.Tick(1_000, true);
        Assert.Null(c.ScreenRemainingSeconds);
    }

    [Fact]
    public void Offline_budget_counts_only_offline_screen_time_and_resets_on_contact()
    {
        var c = new ModeClock(0);
        c.Apply(State(offlineBudget: 60), 0, ReplyKind.Heartbeat);
        c.Tick(1_000, true);
        Assert.Equal(60, c.OfflineRemainingSeconds);

        c.MarkOffline();
        for (long t = 2_000; t <= 61_000; t += 1_000) c.Tick(t, true);
        Assert.Equal(0, c.OfflineRemainingSeconds);
        Assert.True(c.OfflineBudgetExhausted);

        c.Apply(State(offlineBudget: 60), 61_000, ReplyKind.Heartbeat, reportedScreenSeconds: c.ScreenSecondsToReport);
        Assert.False(c.OfflineBudgetExhausted);
        Assert.Equal(60, c.OfflineRemainingSeconds);
    }

    [Fact]
    public void Starts_offline_and_the_budget_survives_a_restart()
    {
        var c = new ModeClock(0, new ClockSnapshot { OfflineBudgetSeconds = 60, OfflineUsedSeconds = 50, PendingScreenSeconds = 50, ServerScreenRemainingSeconds = 1000 });
        Assert.False(c.Online);
        for (long t = 1_000; t <= 9_000; t += 1_000) c.Tick(t, true);
        Assert.False(c.OfflineBudgetExhausted);
        c.Tick(10_000, true);
        Assert.True(c.OfflineBudgetExhausted);

        var snap = c.Snapshot();
        Assert.Equal(60, snap.PendingScreenSeconds, precision: 3);
        Assert.Equal(1000, snap.ServerScreenRemainingSeconds);

        var restarted = new ModeClock(500_000, snap);
        Assert.True(restarted.OfflineBudgetExhausted);
        Assert.Equal(940, restarted.ScreenRemainingSeconds);
        Assert.Null(restarted.SessionId); // a lease never survives a restart
    }

    [Fact]
    public void Game_balance_counts_down_while_gaming()
    {
        var c = new ModeClock(0);
        c.Apply(State("s1", lease: 90, balance: 300), 0, ReplyKind.Start);
        for (long t = 1_000; t <= 60_000; t += 1_000) c.Tick(t, true);
        Assert.Equal(240, c.GameBalanceSeconds);
        Assert.Equal(60, c.GameUsedTodaySeconds);
    }
}

public class CountdownWarningsTests
{
    [Fact]
    public void Each_threshold_fires_once_on_the_way_down()
    {
        var w = new CountdownWarnings(600, 300, 60);
        Assert.Null(w.Check(1000));
        Assert.Equal(600, w.Check(600));
        Assert.Null(w.Check(590));
        Assert.Equal(300, w.Check(299));
        Assert.Equal(60, w.Check(60));
        Assert.Null(w.Check(0));
    }

    [Fact]
    public void Starting_below_several_thresholds_announces_only_the_smallest()
    {
        var w = new CountdownWarnings(600, 300, 60);
        Assert.Equal(300, w.Check(250));
        Assert.Null(w.Check(240));
    }

    [Fact]
    public void Rearms_when_time_is_added()
    {
        var w = new CountdownWarnings(600, 300, 60);
        Assert.Equal(300, w.Check(280));
        Assert.Null(w.Check(2000)); // a parent added time
        Assert.Equal(600, w.Check(590));
        Assert.Null(w.Check(null));
        Assert.Equal(600, w.Check(500));
    }
}

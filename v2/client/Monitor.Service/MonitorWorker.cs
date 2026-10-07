using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Monitor.Core;
using Monitor.Core.Api;
using Monitor.Core.Pipe;

namespace Monitor.Service;

/// <param name="DryRun">Log instead of closing programs or shutting down (for testing on a real account).</param>
/// <param name="MonitorAdmins">Treat administrator accounts like the child's (for testing on a developer PC).</param>
internal sealed record ServiceOptions(bool DryRun, bool MonitorAdmins = false);

internal enum PolicyMode { School, Gaming, Cleared }

/// <summary>
/// The service's main loop. Every second it advances the monotonic countdowns (game lease, screen time,
/// offline budget), switches School/Gaming mode, keeps Edge policies and the process whitelist enforced,
/// starts the agent in each child session, warns, and shuts down when the time is used up.
/// Server calls run in a separate loop (MonitorWorker.Network.cs).
/// </summary>
internal sealed partial class MonitorWorker(ServiceOptions options) : BackgroundService
{
    private const long SessionsEveryMs = 2_000;
    private const long EnforceEveryMs = 5_000;
    private const long AgentsEveryMs = 5_000;
    private const long PersistEveryMs = 30_000;
    private const long StatusEveryMs = 1_000;
    /// <summary>Lets the Windows shell start in a new session before anything is closed there.</summary>
    private const long LogonGraceMs = 30_000;
    /// <summary>Time a new session's agent gets to start and connect.</summary>
    private const long AgentGraceMs = 60_000;
    /// <summary>An agent not connected for this long counts as missing (Gaming ends).</summary>
    private const long AgentMissingAfterMs = 20_000;
    /// <summary>After a start without network, saved limits are acted on only after this long.</summary>
    private const long StartupShutdownGraceMs = 90_000;
    private const long PolicyReassertMs = 10 * 60_000;

    private readonly object gate = new();
    private readonly Channel<NetCommand> commands = Channel.CreateUnbounded<NetCommand>();
    private readonly ActivityBuffer activity = new();
    private readonly List<DeviceEvent> events = [];
    private readonly CountdownWarnings screenWarnings = new(600, 300, 60);
    private readonly CountdownWarnings gameWarnings = new(600, 300, 60);
    private readonly CountdownWarnings offlineWarnings = new(600, 300, 60);
    private readonly Dictionary<int, SessionWatch> watches = [];
    /// <summary>Sessions whose agent said goodbye because Windows is ending the session.</summary>
    private readonly ConcurrentDictionary<int, bool> sessionsEnding = new();
    private readonly HashSet<string> cleanedUserPolicies = [];
    private readonly Dictionary<string, long> lastKillLog = new(StringComparer.OrdinalIgnoreCase);
    private readonly string agentPath = Path.Combine(Paths.InstallDir, Paths.AgentExe);

    private readonly SessionMonitor sessionMonitor = new(options.MonitorAdmins);
    private ApiClient? api;
    private ModeClock clock = null!;
    private DeviceConfig config = new();
    private bool configFetched;
    private ProcessRules rules = null!;
    private AgentHost agents = null!;
    private volatile IReadOnlyList<UserSession> sessions = [];

    private long started;
    private bool wasGaming;
    private bool lastScreenActive;
    private string? schoolReason;
    private bool busy;
    private PolicyMode? appliedPolicy;
    private long policyAppliedAt;
    private bool policyDirty;
    private long lastSessions, lastEnforce, lastAgents, lastPersist, lastStatus, lastScreenshot, captureRequestedAt;
    private long? killEdgeAt;
    private long lastTickForWall, lastWallMs;
    private long? shutdownSince;
    private long lastShutdownAttempt;

    private sealed class SessionWatch(long firstSeen)
    {
        public long FirstSeen { get; } = firstSeen;
        public long LastConnected;
        public long LastLaunch = long.MinValue / 2;
        /// <summary>An agent was connected and has not said goodbye (logoff) since.</summary>
        public bool HadAgent;
        /// <summary>When a connected agent disappeared; reported if the session is still there a little later.</summary>
        public long? MissingSince;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log.Init(Paths.LogDir, "service");
        Log.Info($"Monitor v2 service {Format.Version} starting{(options.DryRun ? " in TEST MODE (nothing is closed, no shutdown)" : "")}" +
            $"{(options.MonitorAdmins ? ", administrators monitored too" : "")}.");
        try
        {
            Store.SecureDataDir();
        }
        catch (Exception ex)
        {
            Log.Error("Could not secure the data folder", ex);
        }

        started = Environment.TickCount64;
        lastScreenshot = started;
        var identity = Store.Load<DeviceIdentity>(Paths.DeviceFile);
        var saved = Store.Load<SavedState>(Paths.StateFile);
        clock = new ModeClock(started, saved?.Clock);
        config = saved?.Config ?? new DeviceConfig();
        rules = BuildRules(config);
        if (identity is { Token.Length: > 0, ServerUrl.Length: > 0 })
        {
            api = new ApiClient(identity.ServerUrl, identity.Token, $"MonitorV2/{Format.Version}");
            Log.Info($"Device \"{identity.DeviceName}\" ({identity.DeviceId}), server {identity.ServerUrl}.");
        }
        else
        {
            Log.Warn("This PC is not enrolled (no device.json). School mode applies and the offline budget counts down.");
        }
        QueueEvent("client_started", new { version = Format.Version, dryRun = options.DryRun });

        agents = new AgentHost(agentPath, OnAgentMessageAsync, (type, detail) => QueueEvent("tamper", new { type, detail }));
        var pipeTask = agents.RunAsync(ct);
        var netTask = NetworkLoopAsync(ct);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Log.Error("Tick failed", ex);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Persist();
            Log.Info("Service stopping.");
        }
        await Task.WhenAll(pipeTask, netTask).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        DetectResume(now);
        if (now - lastSessions >= SessionsEveryMs)
        {
            sessions = sessionMonitor.Refresh();
            lastSessions = now;
            UpdateWatches(now);
        }
        var all = sessions;
        var monitored = all.Where(s => s.Monitored).ToList();
        bool screenActive = monitored.Any(s => s.InUse);

        bool gaming;
        lock (gate)
        {
            clock.Tick(now, screenActive);
            gaming = clock.IsGaming(now);
            lastScreenActive = screenActive;
        }
        if (gaming != wasGaming)
        {
            wasGaming = gaming;
            OnModeChanged(gaming, now);
        }

        if (now - lastAgents >= AgentsEveryMs)
        {
            lastAgents = now;
            SuperviseAgents(now, monitored);
        }
        if (gaming && AgentMissing(now, monitored))
        {
            EndGaming("The Monitor tray app is not running.");
            gaming = false;
        }

        ApplyPolicies(now, gaming, all, monitored);
        if (!gaming) EnforceSchool(now, monitored);
        Warn(gaming, screenActive);
        CheckShutdown(now, screenActive);
        RequestScreenshot(now, screenActive, monitored);

        if (now - lastStatus >= StatusEveryMs)
        {
            lastStatus = now;
            PushStatus(now);
        }
        if (now - lastPersist >= PersistEveryMs)
        {
            lastPersist = now;
            Persist();
        }
    }

    // ---- Mode ----

    private void OnModeChanged(bool gaming, long now)
    {
        if (gaming)
        {
            int balance;
            lock (gate) balance = clock.GameBalanceSeconds;
            schoolReason = null;
            gameWarnings.Reset();
            Log.Info($"Gaming mode ON ({Format.Duration(balance)} of game time left).");
            Toast("🎮 Gaming ON", $"{Format.Duration(balance)} of game time left.");
        }
        else
        {
            string reason;
            lock (gate)
            {
                reason = schoolReason
                    ?? (!clock.Online ? "No connection to the server."
                        : clock.GameBalanceSeconds <= 0 ? "Game time is used up."
                        : "Gaming has ended.");
            }
            schoolReason = null;
            Log.Info($"School mode: {reason}");
            Toast("📘 School mode", reason);
            Broadcast(new PipeMessage { Type = MessageTypes.CloseBrowsers });
            killEdgeAt = now + 3_000; // whatever Edge did not close by itself (streams, preloaded tabs)
            lastEnforce = 0;
            Persist();
        }
    }

    /// <summary>Leaves Gaming mode now and tells the server (which refunds the unused lease).</summary>
    private void EndGaming(string reason)
    {
        string? id;
        lock (gate)
        {
            id = clock.SessionId;
            if (id is null) return;
            clock.EndGaming();
        }
        schoolReason = reason;
        commands.Writer.TryWrite(NetCommand.Stop(id));
    }

    private void ApplyPolicies(long now, bool gaming, IReadOnlyList<UserSession> all, List<UserSession> monitored)
    {
        foreach (var s in monitored)
        {
            if (s.Sid is null || !cleanedUserPolicies.Add(s.Sid)) continue;
            try
            {
                if (EdgePolicies.RemoveUserPolicies(s.Sid)) Log.Info($"Removed Edge policies left in {s.User}'s own registry (from the original app).");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not clean Edge policies of {s.User}: {ex.Message}");
            }
        }

        var active = all.Where(s => s.State == Native.WtsState.Active).ToList();
        var desired = active.Any(s => s.Monitored) ? (gaming ? PolicyMode.Gaming : PolicyMode.School)
            : active.Any(s => !s.Monitored) ? PolicyMode.Cleared // an administrator is at the keyboard
            : gaming ? PolicyMode.Gaming : PolicyMode.School;

        bool due = desired != appliedPolicy
            || (desired == PolicyMode.School && policyDirty)
            || now - policyAppliedAt >= PolicyReassertMs;
        if (!due) return;
        if (appliedPolicy is null && policyAppliedAt != 0 && now - policyAppliedAt < 30_000) return; // retry after a failure

        try
        {
            switch (desired)
            {
                case PolicyMode.School:
                    List<string> sites;
                    lock (gate) sites = [.. config.AllowedSites];
                    EdgePolicies.ApplySchool(sites);
                    break;
                case PolicyMode.Gaming:
                    EdgePolicies.ApplyGaming();
                    break;
                case PolicyMode.Cleared:
                    EdgePolicies.ClearAll();
                    break;
            }
            if (desired != appliedPolicy || policyDirty) Log.Info($"Edge policies: {desired}.");
            appliedPolicy = desired;
            policyDirty = false;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not apply Edge policies ({desired})", ex);
            appliedPolicy = null;
        }
        policyAppliedAt = now;
    }

    private void EnforceSchool(long now, List<UserSession> monitored)
    {
        if (killEdgeAt is long at && now >= at)
        {
            killEdgeAt = null;
            var ids = monitored.Select(s => s.Id).ToHashSet();
            if (options.DryRun) Log.Info("[test mode] Would close Edge after leaving Gaming mode.");
            else if (Enforcer.KillByName("msedge", ids) is int n and > 0) Log.Info($"Closed {n} Edge process(es) left after Gaming mode.");
        }

        if (now - lastEnforce < EnforceEveryMs) return;
        lastEnforce = now;
        var sessionIds = monitored
            .Where(s => watches.TryGetValue(s.Id, out var w) && now - w.FirstSeen >= LogonGraceMs)
            .Select(s => s.Id)
            .ToHashSet();
        ProcessRules r;
        lock (gate) r = rules;
        foreach (var v in Enforcer.FindViolations(r, sessionIds))
        {
            bool log = !lastKillLog.TryGetValue(v.Name, out var last) || now - last >= 60_000;
            if (log) lastKillLog[v.Name] = now;
            if (options.DryRun)
            {
                if (log) Log.Info($"[test mode] Would close {v.Name} ({v.Path ?? "path unknown"}) in session {v.SessionId}.");
                continue;
            }
            if (!Enforcer.Kill(v.Pid)) continue;
            if (log) Log.Info($"Closed {v.Name} ({v.Path ?? "path unknown"}) in session {v.SessionId}: not allowed in School mode.");
            lock (gate) activity.Add(now, "school", v.Name, null, null, blocked: 1);
        }
    }

    // ---- Agents ----

    private void UpdateWatches(long now)
    {
        var current = sessions.Select(s => s.Id).ToHashSet();
        foreach (var id in current)
            if (!watches.ContainsKey(id)) watches[id] = new SessionWatch(now);
        foreach (var id in watches.Keys.Where(id => !current.Contains(id)).ToList())
        {
            watches.Remove(id);
            sessionsEnding.TryRemove(id, out _);
        }
    }

    private void SuperviseAgents(long now, List<UserSession> monitored)
    {
        if (!File.Exists(agentPath)) return;
        var connected = agents.Connections.Select(c => c.SessionId).ToHashSet();
        foreach (var s in monitored.Where(s => s.State == Native.WtsState.Active))
        {
            if (!watches.TryGetValue(s.Id, out var w)) continue;
            if (connected.Contains(s.Id))
            {
                w.HadAgent = true;
                w.LastConnected = now;
                continue;
            }
            if (Enforcer.RunningInSession(Path.GetFileNameWithoutExtension(Paths.AgentExe), s.Id).Any()) continue; // starting up

            if (w.HadAgent)
            {
                w.HadAgent = false; // one report per disappearance
                if (!sessionsEnding.ContainsKey(s.Id)) w.MissingSince ??= now;
            }
            if (now - w.LastLaunch < 10_000) continue;
            w.LastLaunch = now;
            try
            {
                int pid = Native.StartAsSessionUser(s.Id, agentPath);
                Log.Info($"Started the agent for {s.User} (session {s.Id}, pid {pid}).");
            }
            catch (Exception ex)
            {
                Log.Error($"Could not start the agent in session {s.Id}", ex);
            }
        }

        // Report a closed agent only if the session is still there a little later (not a logoff).
        foreach (var s in monitored)
        {
            if (!watches.TryGetValue(s.Id, out var w) || w.MissingSince is not long since) continue;
            if (now - since < 15_000) continue;
            w.MissingSince = null;
            Log.Warn($"The agent of {s.User} (session {s.Id}) was closed; it has been started again.");
            QueueEvent("agent_missing", new { user = s.User, session = s.Id });
        }
    }

    private bool AgentMissing(long now, List<UserSession> monitored)
    {
        if (!File.Exists(agentPath)) return false;
        var connected = agents.Connections.Select(c => c.SessionId).ToHashSet();
        return monitored.Any(s => s.InUse
            && !connected.Contains(s.Id)
            && watches.TryGetValue(s.Id, out var w)
            && now > Math.Max(w.LastConnected + AgentMissingAfterMs, w.FirstSeen + AgentGraceMs));
    }

    private Task OnAgentMessageAsync(AgentConnection conn, PipeMessage msg)
    {
        long now = Environment.TickCount64;
        var session = sessions.FirstOrDefault(s => s.Id == conn.SessionId);
        switch (msg.Type)
        {
            case MessageTypes.Bye:
                // Windows is ending the session (logoff, shutdown): the agent closing is expected.
                sessionsEnding[conn.SessionId] = true;
                break;
            case MessageTypes.Hello:
                Log.Info($"Agent {msg.Version} says hello from session {conn.SessionId}.");
                return conn.SendAsync(new PipeMessage { Type = MessageTypes.Status, Status = BuildStatus(conn.SessionId, now) });
            case MessageTypes.Sample when msg.Sample is not null && session is { Monitored: true, InUse: true }:
                OnSample(now, conn, msg.Sample);
                break;
            case MessageTypes.Game when session is { Monitored: true }:
                if (msg.On == true) RequestStart(now);
                else EndGaming("You turned gaming off.");
                break;
            case MessageTypes.Screenshot when msg.Jpeg is not null && session is { Monitored: true }:
                if (captureRequestedAt == 0 || now - captureRequestedAt > 60_000) break; // only answers to our own requests
                captureRequestedAt = 0;
                try
                {
                    var bytes = Convert.FromBase64String(msg.Jpeg);
                    if (bytes.Length is > 0 and <= 8 * 1024 * 1024) commands.Writer.TryWrite(NetCommand.Screenshot(bytes));
                }
                catch (FormatException)
                {
                }
                break;
        }
        return Task.CompletedTask;
    }

    private void RequestStart(long now)
    {
        lock (gate)
        {
            if (busy || clock.IsGaming(now)) return;
            busy = true;
        }
        commands.Writer.TryWrite(NetCommand.Start);
    }

    private void OnSample(long now, AgentConnection conn, Sample sample)
    {
        double seconds = Math.Clamp(sample.Seconds, 0, 15);
        var fg = sample.Foreground;
        bool gaming;
        List<string> blockedTitles;
        lock (gate)
        {
            gaming = clock.IsGaming(now);
            var mode = gaming ? "gaming" : "school";
            if (fg is { App.Length: > 0 }) activity.Add(now, mode, fg.App, fg.Site, fg.Title, seconds);
            foreach (var a in sample.Audio.Where(a => a.App.Length > 0).DistinctBy(a => a.App, StringComparer.OrdinalIgnoreCase))
            {
                if (fg is not null && string.Equals(a.App, fg.App, StringComparison.OrdinalIgnoreCase))
                    activity.Add(now, mode, fg.App, fg.Site, fg.Title, audioSeconds: seconds);
                else
                    activity.Add(now, mode, a.App, null, null, audioSeconds: seconds);
            }
            blockedTitles = config.BlockedTitles;
        }

        // Blocked titles: exceptions inside allowed apps/sites (e.g. a Google Doodle game). Windows' own processes are never closed.
        if (gaming || fg is null) return;
        if (ProcessRules.MatchBlockedTitle(fg.Title, blockedTitles) is not string fragment) return;
        if (ProcessRules.BaseWindowsProcesses.Contains(ProcessRules.NormalizeName(fg.App))) return;
        if (Native.SessionOf(fg.Pid) != conn.SessionId) return;
        if (options.DryRun)
        {
            Log.Info($"[test mode] Would close {fg.App}: title contains \"{fragment}\".");
            return;
        }
        if (Enforcer.Kill(fg.Pid))
        {
            Log.Info($"Closed {fg.App}: title contains \"{fragment}\" (blocked in School mode).");
            lock (gate) activity.Add(now, "school", fg.App, fg.Site, fg.Title, blocked: 1);
        }
    }

    private AgentStatus BuildStatus(int sessionId, long now)
    {
        var s = sessions.FirstOrDefault(x => x.Id == sessionId);
        lock (gate)
        {
            return new AgentStatus
            {
                Mode = clock.IsGaming(now) ? "gaming" : "school",
                Monitored = s?.Monitored ?? true,
                Enrolled = api is not null,
                Online = clock.Online,
                Busy = busy,
                GameBalanceSeconds = clock.GameBalanceSeconds,
                GameUsedTodaySeconds = clock.GameUsedTodaySeconds,
                LeaseRemainingSeconds = clock.LeaseRemainingSeconds(now),
                ScreenLimitSeconds = clock.LastState?.Screen.LimitSeconds,
                ScreenRemainingSeconds = clock.ScreenRemainingSeconds,
                OfflineRemainingSeconds = clock.Online ? null : clock.OfflineRemainingSeconds,
                Notice = api is null ? "This PC is not enrolled yet."
                    : !clock.Online ? "No connection to the server. Gaming is not available."
                    : options.DryRun ? "Test mode: nothing is closed and the PC is not shut down."
                    : null,
            };
        }
    }

    private void PushStatus(long now)
    {
        foreach (var conn in agents.Connections)
            _ = conn.SendAsync(new PipeMessage { Type = MessageTypes.Status, Status = BuildStatus(conn.SessionId, now) });
    }

    /// <summary>Notification in every child session.</summary>
    private void Toast(string title, string text, bool warning = false)
    {
        var monitoredIds = sessions.Where(s => s.Monitored).Select(s => s.Id).ToHashSet();
        foreach (var conn in agents.Connections.Where(c => monitoredIds.Contains(c.SessionId)))
            _ = conn.SendAsync(new PipeMessage { Type = MessageTypes.Toast, Title = title, Text = text, Warning = warning });
    }

    private void Broadcast(PipeMessage message)
    {
        foreach (var conn in agents.Connections) _ = conn.SendAsync(message);
    }

    // ---- Warnings, shutdown, screenshots ----

    private void Warn(bool gaming, bool screenActive)
    {
        int? screenLeft, offlineLeft;
        int gameLeft;
        bool online;
        lock (gate)
        {
            screenLeft = clock.ScreenRemainingSeconds;
            gameLeft = clock.GameBalanceSeconds;
            online = clock.Online;
            offlineLeft = clock.OfflineRemainingSeconds;
        }

        if (screenActive && screenLeft > 0 && screenWarnings.Check(screenLeft) is int s)
            Toast($"⏰ {s / 60} min of screen time left", "The PC shuts down when today's screen time is used up. Save your work.", warning: true);
        else if (screenLeft is null) screenWarnings.Reset();

        if (gaming)
        {
            if (gameWarnings.Check(gameLeft) is int g)
                Toast($"⏳ {g / 60} min of game time left", "Then the PC goes back to School mode.", warning: true);
        }

        if (online) offlineWarnings.Reset();
        else if (screenActive && offlineLeft > 0 && offlineWarnings.Check(offlineLeft) is int o)
            Toast("No connection to the server", $"The PC shuts down in {o / 60} min unless the connection comes back.", warning: true);
    }

    private void CheckShutdown(long now, bool screenActive)
    {
        string? reason = null;
        if (screenActive && (clockHeardFromServer || now - started >= StartupShutdownGraceMs))
        {
            lock (gate)
            {
                if (clock.ScreenRemainingSeconds == 0) reason = "Today's screen time is used up.";
                else if (clock.OfflineBudgetExhausted) reason = "The PC was offline for too long.";
            }
        }
        if (reason is null)
        {
            shutdownSince = null;
            return;
        }
        if (shutdownSince is null)
        {
            shutdownSince = now;
            Log.Warn($"Shutting down: {reason}");
            QueueEvent("shutdown", new { reason });
            commands.Writer.TryWrite(NetCommand.Flush);
            Toast("🔴 The PC is shutting down", reason, warning: true);
            Persist();
        }
        if (now - shutdownSince < 5_000 || now - lastShutdownAttempt < 15_000) return; // let the event go out first
        lastShutdownAttempt = now;
        if (options.DryRun)
        {
            Log.Info("[test mode] Would shut down now.");
            return;
        }
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            using var p = Process.Start(new ProcessStartInfo(exe, "/s /f /t 0") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("shutdown.exe failed", ex);
        }
    }

    private void RequestScreenshot(long now, bool screenActive, List<UserSession> monitored)
    {
        int minutes;
        lock (gate) minutes = config.ScreenshotIntervalMinutes;
        if (minutes <= 0 || !screenActive || now - lastScreenshot < minutes * 60_000L) return;
        var inUse = monitored.Where(s => s.InUse).Select(s => s.Id).ToHashSet();
        var target = agents.Connections.FirstOrDefault(c => inUse.Contains(c.SessionId));
        if (target is null) return;
        lastScreenshot = now;
        captureRequestedAt = now;
        _ = target.SendAsync(new PipeMessage { Type = MessageTypes.Capture });
    }

    // ---- Helpers ----

    /// <summary>After sleep or a clock change, check in with the server right away.</summary>
    private void DetectResume(long now)
    {
        long wall = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        if (lastTickForWall != 0)
        {
            long dt = now - lastTickForWall, dw = wall - lastWallMs;
            if (dt > 10_000 || Math.Abs(dw - dt) > 10_000)
            {
                Log.Info("Resumed from sleep (or the clock changed): checking in with the server.");
                commands.Writer.TryWrite(NetCommand.HeartbeatNow);
            }
        }
        lastTickForWall = now;
        lastWallMs = wall;
    }

    private static ProcessRules BuildRules(DeviceConfig cfg) =>
        new(cfg.AllowedApps, Paths.InstallDir, TrustedFiles.IsSystemOwned);

    private void QueueEvent(string type, object? detail)
    {
        lock (gate)
        {
            events.Add(new DeviceEvent { Type = type, Detail = detail });
            if (events.Count > 200) events.RemoveRange(0, events.Count - 200);
        }
    }

    private void Persist()
    {
        try
        {
            SavedState state;
            lock (gate)
            {
                if (clock is null) return;
                state = new SavedState { Clock = clock.Snapshot(), Config = config };
            }
            Store.Save(Paths.StateFile, state);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save state", ex);
        }
    }
}

using Monitor.Core;
using Monitor.Core.Api;

namespace Monitor.Service;

/// <summary>Server calls, one at a time: heartbeats every 30 s, plus GAME ON/OFF, config, activity, events, screenshots.</summary>
internal sealed partial class MonitorWorker
{
    private const long RetryMs = 10_000;

    /// <summary>At least one server call succeeded since the service started.</summary>
    private volatile bool clockHeardFromServer;
    private int failures;

    private sealed record NetCommand(string Kind, string? SessionId = null, byte[]? Image = null)
    {
        public static readonly NetCommand Start = new("start");
        public static readonly NetCommand HeartbeatNow = new("heartbeat");
        public static readonly NetCommand Flush = new("flush");
        public static NetCommand Stop(string sessionId) => new("stop", sessionId);
        public static NetCommand Screenshot(byte[] jpeg) => new("screenshot", Image: jpeg);
    }

    private async Task NetworkLoopAsync(CancellationToken ct)
    {
        long nextHeartbeat = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!commands.Reader.TryRead(out var cmd))
            {
                long wait = nextHeartbeat - Environment.TickCount64;
                if (wait > 0)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(wait));
                    try
                    {
                        cmd = await commands.Reader.ReadAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                    }
                }
            }

            try
            {
                switch (cmd?.Kind)
                {
                    case null:
                        bool ok = await HeartbeatAsync(ct);
                        nextHeartbeat = Environment.TickCount64 + (ok ? HeartbeatIntervalMs() : RetryMs);
                        if (ok)
                        {
                            await SyncConfigAsync(ct);
                            await FlushAsync(ct);
                        }
                        break;
                    case "heartbeat":
                        nextHeartbeat = 0;
                        break;
                    case "start":
                        await StartGameAsync(ct);
                        break;
                    case "stop":
                        await StopGameAsync(cmd.SessionId, ct);
                        break;
                    case "flush":
                        await FlushAsync(ct);
                        break;
                    case "screenshot":
                        await SendScreenshotAsync(cmd.Image!, ct);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"Server call '{cmd?.Kind ?? "heartbeat"}' failed", ex);
            }
        }
    }

    private long HeartbeatIntervalMs()
    {
        int seconds;
        lock (gate) seconds = clock.LastState?.Timing.HeartbeatSeconds ?? 30;
        return Math.Clamp(seconds, 10, 60) * 1000L;
    }

    private async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        if (api is null)
        {
            lock (gate) clock.MarkOffline();
            return false;
        }
        long t0 = Environment.TickCount64;
        string? sent;
        int reported;
        HeartbeatRequest request;
        lock (gate)
        {
            sent = clock.SessionId;
            reported = clock.ScreenSecondsToReport;
            request = new HeartbeatRequest
            {
                SessionId = sent,
                ScreenActive = lastScreenActive,
                ScreenSeconds = reported,
                ClientVersion = Format.Version,
            };
        }
        try
        {
            var state = await api.HeartbeatAsync(request, ct);
            lock (gate)
            {
                if (sent is not null && clock.SessionId == sent && !state.IsGaming)
                    schoolReason ??= state.Game.BalanceSeconds <= 0 ? "Game time is used up." : "A parent ended gaming.";
                clock.Apply(state, t0, ReplyKind.Heartbeat, reported, sent);
            }
            if (failures > 0 || !clockHeardFromServer) Log.Info("Connected to the server.");
            failures = 0;
            clockHeardFromServer = true;
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (gate) clock.MarkOffline();
            failures++;
            if (failures == 1 || failures % 60 == 0)
            {
                var hint = ex is ApiException { Status: System.Net.HttpStatusCode.Unauthorized }
                    ? " The device token was rejected: enroll this PC again."
                    : "";
                Log.Warn($"Heartbeat failed ({failures}x): {ex.Message}{hint}");
            }
            return false;
        }
    }

    private async Task SyncConfigAsync(CancellationToken ct)
    {
        int version;
        lock (gate) version = clock.LastState?.ConfigVersion ?? -1;
        if (api is null || (configFetched && version == config.Version)) return;
        try
        {
            var cfg = await api.GetConfigAsync(ct);
            lock (gate)
            {
                config = cfg;
                rules = BuildRules(cfg);
                configFetched = true;
                policyDirty = true;
            }
            Log.Info($"Config v{cfg.Version}: {cfg.AllowedApps.Count} apps, {cfg.AllowedSites.Count} sites, " +
                $"{cfg.BlockedTitles.Count} blocked titles, screenshots every {cfg.ScreenshotIntervalMinutes} min.");
            Persist();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Could not fetch the config: {ex.Message}");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (api is null) return;
        List<DeviceEvent> pending;
        lock (gate)
        {
            pending = [.. events];
            events.Clear();
        }
        if (pending.Count > 0)
        {
            try
            {
                await api.PostEventsAsync(pending, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"Could not send {pending.Count} event(s): {ex.Message}");
                lock (gate)
                {
                    events.InsertRange(0, pending);
                    if (events.Count > 200) events.RemoveRange(0, events.Count - 200);
                }
            }
        }

        ActivityBuffer.Batch batch;
        lock (gate) batch = activity.Take();
        if (batch.Count == 0) return;
        var items = batch.ToItems(Environment.TickCount64);
        try
        {
            if (items.Count > 0) await api.PostActivityAsync(items, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Could not send activity: {ex.Message}");
            lock (gate) activity.Restore(batch);
        }
    }

    private async Task StartGameAsync(CancellationToken ct)
    {
        try
        {
            if (api is null)
            {
                Toast("Gaming is not available", "This PC is not enrolled yet.", warning: true);
                return;
            }
            long t0 = Environment.TickCount64;
            var (outcome, state) = await api.StartGameAsync(ct);
            lock (gate) clock.Apply(state, t0, ReplyKind.Start);
            clockHeardFromServer = true;
            if (outcome == StartOutcome.NoBalance)
            {
                Log.Info("GAME ON refused: no game time left.");
                Toast("No game time left", "Ask a parent for more game time.", warning: true);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (gate) clock.MarkOffline();
            Log.Warn($"GAME ON failed: {ex.Message}");
            Toast("Gaming is not available", "No connection to the server. Gaming can't start offline.", warning: true);
        }
        finally
        {
            lock (gate) busy = false;
        }
    }

    private async Task StopGameAsync(string? sessionId, CancellationToken ct)
    {
        if (api is null) return;
        long t0 = Environment.TickCount64;
        try
        {
            var state = await api.StopGameAsync(sessionId, ct);
            lock (gate)
            {
                // A GAME ON pressed meanwhile is queued after this stop, so this reply never ends a newer session.
                if (clock.SessionId is null || clock.SessionId == sessionId) clock.Apply(state, t0, ReplyKind.Stop);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The session ends anyway: the next heartbeat does not renew it.
            Log.Warn($"GAME OFF could not reach the server: {ex.Message}");
        }
    }

    private async Task SendScreenshotAsync(byte[] jpeg, CancellationToken ct)
    {
        if (api is null) return;
        try
        {
            await api.PostScreenshotAsync(jpeg, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Could not send a screenshot: {ex.Message}");
        }
    }
}

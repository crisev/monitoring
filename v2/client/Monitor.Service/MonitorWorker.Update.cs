using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Hosting.WindowsServices;
using Monitor.Core;

namespace Monitor.Service;

/// <summary>
/// Automatic updates. When a parent clicks Update, the heartbeat reply carries an <see cref="UpdateOffer"/>.
/// The service downloads the zip into its protected data folder, checks size, SHA-256 and the version
/// inside, and then (not while gaming) starts update.ps1 as SYSTEM. It uses the update.ps1 of the
/// version installed now, which is known to work. The script stops this service, keeps a backup,
/// copies the new files and starts the service. If the new version does not reach the server within
/// 2 minutes, the script puts the backup back. Whichever version starts next reports the outcome.
/// </summary>
internal sealed partial class MonitorWorker
{
    private List<string> failedUpdates = [];
    /// <summary>Set when this version was just installed by an update: reported after the first successful heartbeat.</summary>
    private UpdatePending? justUpdated;
    private int updating;
    private bool updateLaunched;
    private bool loggedNotInstalled;

    /// <summary>At startup: did an update just happen (we are the new version) or roll back (we are the old one)?</summary>
    private void CheckUpdateOutcome()
    {
        var pending = Store.Load<UpdatePending>(Updates.PendingFile);
        if (pending is null) return;
        if (pending.To == Format.Version)
        {
            justUpdated = pending;
            Log.Info($"Started as the new version {pending.To} (updated from {pending.From}); confirming once the server answers.");
            return;
        }
        var result = Store.Load<UpdateResult>(Updates.ResultFile);
        var reason = result?.Message ?? "the new version did not start";
        Log.Warn($"The update to {pending.To} did not work and {Format.Version} is running again: {reason}");
        QueueEvent("update_failed", new { from = Format.Version, to = pending.To, reason });
        failedUpdates.Add(pending.OfferId);
        TryDelete(Updates.PendingFile);
        TryDelete(Updates.ResultFile);
        Persist();
    }

    /// <summary>After a successful heartbeat: tell update.ps1 (and the server) that the new version works.</summary>
    private void ConfirmUpdate()
    {
        if (justUpdated is not { } done) return;
        justUpdated = null;
        try
        {
            File.WriteAllText(Updates.OkFile, Format.Version);
        }
        catch (Exception ex)
        {
            Log.Error("Could not confirm the update", ex);
        }
        QueueEvent("updated", new { from = done.From, to = done.To });
        TryDelete(Updates.PendingFile);
        Log.Info($"Update to {done.To} confirmed.");
    }

    /// <summary>After a successful heartbeat: start installing an offered update, in the background.</summary>
    private void MaybeStartUpdate(CancellationToken ct)
    {
        UpdateOffer? offer;
        bool gaming;
        lock (gate)
        {
            offer = clock.LastState?.Update;
            gaming = clock.IsGaming(Environment.TickCount64);
        }
        if (offer is null || updateLaunched || justUpdated is not null || gaming || shutdownSince is not null) return;
        if (!Updates.IsNewer(offer.Version, Format.Version) || failedUpdates.Contains(offer.Id)) return;
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            if (!loggedNotInstalled) Log.Info($"Update {offer.Version} offered, but this is not the installed service: ignored.");
            loggedNotInstalled = true;
            return;
        }
        if (Interlocked.Exchange(ref updating, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await InstallUpdateAsync(offer, ct);
            }
            finally
            {
                Interlocked.Exchange(ref updating, 0);
            }
        }, ct);
    }

    private async Task InstallUpdateAsync(UpdateOffer offer, CancellationToken ct)
    {
        try
        {
            Updates.Validate(offer);
            var currentScript = Path.Combine(Paths.InstallDir, Updates.Script);
            if (!File.Exists(currentScript))
                throw new UpdateException($"this version has no {Updates.Script}; install {offer.Version} by hand once");

            var dir = Path.Combine(Updates.Dir, offer.Version);
            CleanOldDownloads(keep: offer.Version);
            var zip = Path.Combine(dir, $"MonitorV2-{offer.Version}.zip");
            if (!Updates.Matches(zip, offer.Sha256, offer.Size))
            {
                Log.Info($"Downloading version {offer.Version} ({offer.Size / 1024 / 1024} MB)...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"MonitorV2/{Format.Version}");
                await Updates.DownloadAsync(http, offer, zip, ct);
            }
            Log.Info($"Version {offer.Version} downloaded; size and SHA-256 match.");

            var unpacked = Path.Combine(dir, "pkg");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
            ZipFile.ExtractToDirectory(zip, unpacked); // refuses entries that would land outside the folder
            var appDir = Updates.FindAppDir(unpacked) ?? throw new UpdateException("the package has no app folder");
            var inside = FileVersionInfo.GetVersionInfo(Path.Combine(appDir, Paths.ServiceExe)).ProductVersion?.Split('+')[0];
            if (inside != offer.Version) throw new UpdateException($"the package contains version {inside}, not {offer.Version}");

            lock (gate)
            {
                if (clock.IsGaming(Environment.TickCount64))
                {
                    Log.Info($"Version {offer.Version} is ready; it installs when gaming ends.");
                    return; // tried again after a later heartbeat; the download is kept
                }
            }

            var script = Path.Combine(Updates.Dir, Updates.Script);
            File.Copy(currentScript, script, overwrite: true);
            TryDelete(Updates.OkFile);
            TryDelete(Updates.ResultFile);
            Store.Save(Updates.PendingFile, new UpdatePending { From = Format.Version, To = offer.Version, OfferId = offer.Id });
            if (api is not null)
            {
                try
                {
                    await api.PostEventsAsync([new() { Type = "update_started", Detail = new { from = Format.Version, to = offer.Version } }], ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Log.Warn($"Could not report the update start: {ex.Message}");
                }
            }
            Persist();

            var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" " +
                $"-Source \"{appDir}\" -Version {offer.Version} -From {Format.Version} " +
                $"-InstallDir \"{Paths.InstallDir}\" -DataDir \"{Paths.DataDir}\"";
            using var p = Process.Start(new ProcessStartInfo(powershell, args) { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new UpdateException("could not start the installer");
            updateLaunched = true;
            Log.Info($"Installing version {offer.Version}: {Updates.Script} (pid {p.Id}) restarts the service.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var reason = ex is UpdateException or InvalidDataException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log.Error($"Update to {offer.Version} failed", ex);
            TryDelete(Updates.PendingFile);
            lock (gate) failedUpdates.Add(offer.Id);
            QueueEvent("update_failed", new { from = Format.Version, to = offer.Version, reason });
            Persist();
        }
    }

    /// <summary>Removes downloads of other versions (keeps the backup and the script).</summary>
    private static void CleanOldDownloads(string keep)
    {
        if (!Directory.Exists(Updates.Dir)) return;
        foreach (var d in Directory.GetDirectories(Updates.Dir))
        {
            var name = Path.GetFileName(d);
            if (name != keep && Updates.IsNewer(name, "0.0.0"))
            {
                try { Directory.Delete(d, recursive: true); } catch { }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

using Monitor.Core.Api;

namespace Monitor.Core;

/// <summary>
/// Collects foreground/audio samples and blocked attempts into one-minute buckets per
/// (mode, app, site, title), ready for POST /api/device/activity. Times are monotonic ticks (ms);
/// <c>secondsAgo</c> is worked out when a batch is sent, so the PC's wall clock is never needed.
/// Not thread-safe: the caller serializes access.
/// </summary>
public sealed class ActivityBuffer(int maxEntries = 20_000)
{
    public const int MaxItemsPerRequest = 1000;
    private const long MaxAgeMs = 7L * 86_400_000 - 3_600_000; // the server accepts up to 7 days

    private readonly Dictionary<Key, Entry> entries = [];

    public int Count => entries.Count;

    /// <param name="now">Tick at the end of the sampled period.</param>
    /// <param name="seconds">Foreground seconds in that period.</param>
    public void Add(long now, string mode, string app, string? site, string? title,
        double seconds = 0, double audioSeconds = 0, int blocked = 0)
    {
        if (string.IsNullOrWhiteSpace(app)) return;
        var key = new Key(now / 60_000, mode, ProcessRules.NormalizeName(app),
            Clip(site, 300), Clip(title, 300));
        if (!entries.TryGetValue(key, out var e))
        {
            if (entries.Count >= maxEntries) DropOldest();
            e = new Entry { StartTick = now - (long)(Math.Max(seconds, audioSeconds) * 1000) };
            entries[key] = e;
        }
        e.Seconds += seconds;
        e.AudioSeconds += audioSeconds;
        e.Blocked += blocked;
    }

    /// <summary>Removes up to <paramref name="max"/> of the oldest entries. Give them back with <see cref="Restore"/> if sending fails.</summary>
    public Batch Take(int max = MaxItemsPerRequest)
    {
        var taken = entries.OrderBy(kv => kv.Value.StartTick).Take(max).ToList();
        foreach (var kv in taken) entries.Remove(kv.Key);
        return new Batch(taken);
    }

    public void Restore(Batch batch)
    {
        foreach (var (key, e) in batch.Entries)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                existing.StartTick = Math.Min(existing.StartTick, e.StartTick);
                existing.Seconds += e.Seconds;
                existing.AudioSeconds += e.AudioSeconds;
                existing.Blocked += e.Blocked;
            }
            else if (entries.Count < maxEntries)
            {
                entries[key] = e;
            }
        }
    }

    private void DropOldest()
    {
        var oldest = entries.MinBy(kv => kv.Value.StartTick);
        entries.Remove(oldest.Key);
    }

    private static string Clip(string? s, int max)
    {
        s = s?.Trim() ?? "";
        return s.Length > max ? s[..max] : s;
    }

    internal readonly record struct Key(long Minute, string Mode, string App, string Site, string Title);

    internal sealed class Entry
    {
        public long StartTick;
        public double Seconds;
        public double AudioSeconds;
        public int Blocked;
    }

    public sealed class Batch
    {
        internal Batch(List<KeyValuePair<Key, Entry>> entries) =>
            Entries = entries.Select(kv => (kv.Key, kv.Value)).ToList();

        internal List<(Key Key, Entry Entry)> Entries { get; }

        public int Count => Entries.Count;

        /// <summary>The batch as API items, with <c>secondsAgo</c> relative to <paramref name="now"/>.</summary>
        public List<ActivityItem> ToItems(long now) =>
            Entries
                .Where(x => now - x.Entry.StartTick <= MaxAgeMs)
                .Select(x => new ActivityItem
                {
                    SecondsAgo = (int)Math.Max(0, (now - x.Entry.StartTick) / 1000),
                    Mode = x.Key.Mode,
                    App = x.Key.App,
                    Site = x.Key.Site.Length > 0 ? x.Key.Site : null,
                    Title = x.Key.Title.Length > 0 ? x.Key.Title : null,
                    Seconds = (int)Math.Clamp(Math.Round(x.Entry.Seconds), 0, 600),
                    AudioSeconds = (int)Math.Clamp(Math.Round(x.Entry.AudioSeconds), 0, 600),
                    Blocked = Math.Clamp(x.Entry.Blocked, 0, 1000),
                })
                .Where(i => i.Seconds > 0 || i.AudioSeconds > 0 || i.Blocked > 0)
                .ToList();
    }
}

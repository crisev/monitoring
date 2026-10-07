using Monitor.Core;

namespace Monitor.Core.Tests;

public class ActivityBufferTests
{
    [Fact]
    public void Samples_of_the_same_minute_are_merged()
    {
        var b = new ActivityBuffer();
        b.Add(60_005_000, "school", "msedge.exe", "pbinfo.ro", "Problema", seconds: 5);
        b.Add(60_010_000, "school", "msedge", "pbinfo.ro", "Problema", seconds: 5, audioSeconds: 5);
        b.Add(60_010_000, "school", "Code", null, "main.cpp", seconds: 5);

        var items = b.Take().ToItems(now: 60_070_000);
        Assert.Equal(2, items.Count);
        var edge = items.Single(i => i.App == "msedge");
        Assert.Equal(10, edge.Seconds);
        Assert.Equal(5, edge.AudioSeconds);
        Assert.Equal("pbinfo.ro", edge.Site);
        Assert.Equal(70, edge.SecondsAgo); // the stretch started at tick 60 000 000
        Assert.Null(items.Single(i => i.App == "Code").Site);
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void Different_minutes_and_modes_stay_separate()
    {
        var b = new ActivityBuffer();
        b.Add(60_000, "school", "msedge", null, null, seconds: 5);
        b.Add(125_000, "school", "msedge", null, null, seconds: 5);
        b.Add(125_000, "gaming", "msedge", null, null, seconds: 5);
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void A_failed_batch_can_be_restored()
    {
        var b = new ActivityBuffer();
        b.Add(10_000, "school", "game", null, null, blocked: 1);
        var batch = b.Take();
        b.Add(12_000, "school", "game", null, null, blocked: 1);
        b.Restore(batch);

        var items = b.Take().ToItems(20_000);
        Assert.Single(items);
        Assert.Equal(2, items[0].Blocked);
        Assert.Equal(10, items[0].SecondsAgo);
    }

    [Fact]
    public void Take_returns_the_oldest_first_and_respects_the_limit()
    {
        var b = new ActivityBuffer();
        for (int i = 0; i < 5; i++) b.Add(i * 60_000L + 1_000, "school", $"app{i}", null, null, seconds: 1);
        var first = b.Take(max: 2).ToItems(400_000);
        Assert.Equal(["app0", "app1"], first.Select(i => i.App));
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void Empty_entries_and_oldest_overflow_are_dropped()
    {
        var b = new ActivityBuffer(maxEntries: 2);
        b.Add(1_000, "school", "a", null, null);
        b.Add(61_000, "school", "b", null, null, seconds: 1);
        b.Add(121_000, "school", "c", null, null, seconds: 1);
        var items = b.Take().ToItems(200_000);
        Assert.Equal(["b", "c"], items.Select(i => i.App));
    }
}

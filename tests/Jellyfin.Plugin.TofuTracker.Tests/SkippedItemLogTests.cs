using Jellyfin.Plugin.TofuTracker.Core;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class SkippedItemLogTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-02T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void An_item_is_reported_once_per_run_but_listed_with_its_latest_time()
    {
        var log = new SkippedItemLog(_time);

        Assert.True(log.Record("a", "Home video", "no ids"));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(log.Record("a", "Home video", "no ids"));

        var item = Assert.Single(log.Recent());
        Assert.Equal("Home video", item.Name);
        Assert.Equal("no ids", item.Reason);
        Assert.Equal(_time.GetUtcNow(), item.At);
    }

    [Fact]
    public void The_list_keeps_the_newest_twenty_distinct_items_newest_first()
    {
        var log = new SkippedItemLog(_time);

        for (var i = 1; i <= 25; i++)
        {
            Assert.True(log.Record($"k{i}", $"Item {i}", "no ids"));
        }

        var names = log.Recent().Select(i => i.Name).ToList();
        Assert.Equal(SkippedItemLog.Capacity, names.Count);
        Assert.Equal("Item 25", names[0]);
        Assert.Equal("Item 6", names[^1]);

        // Seeing an older item again moves it to the front.
        log.Record("k10", "Item 10", "no ids");
        Assert.Equal("Item 10", log.Recent()[0].Name);
        Assert.Equal(SkippedItemLog.Capacity, log.Recent().Count);
    }

    [Fact]
    public void An_item_that_fell_off_the_list_is_still_not_reported_again()
    {
        var log = new SkippedItemLog(_time);
        log.Record("first", "First", "no ids");
        for (var i = 0; i < 30; i++)
        {
            log.Record($"other{i}", $"Other {i}", "no ids");
        }

        Assert.False(log.Record("first", "First", "no ids"));
    }
}

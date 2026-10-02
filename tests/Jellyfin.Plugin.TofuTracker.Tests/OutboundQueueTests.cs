using Jellyfin.Plugin.TofuTracker.Core;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class OutboundQueueTests : IDisposable
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-02T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private OutboundQueue Queue(int durable = 1000, int ephemeral = 200)
        => new(_dir.Path, Samples.Logger, _time) { DurableCapacity = durable, EphemeralCapacity = ephemeral };

    private ScrobbleEvent Event(string action, string session) => Samples.Event(action, session, _time.GetUtcNow());

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Watched_events_survive_a_restart_but_live_events_do_not()
    {
        var first = Queue();
        first.Enqueue(UserA, Event(EventActions.Watched, "w1"));
        first.Enqueue(UserA, Event(EventActions.Start, "s1"));
        first.Enqueue(UserA, Event(EventActions.Progress, "s1"));

        var second = Queue();

        Assert.Equal(1, second.Count);
        var restored = Assert.Single(second.Peek(UserA, 10));
        Assert.Equal("w1", restored.Event.SessionId);
        Assert.Equal(EventActions.Watched, restored.Event.Action);
    }

    [Fact]
    public void Completing_a_watched_event_removes_it_from_disk()
    {
        var queue = Queue();
        queue.Enqueue(UserA, Event(EventActions.Watched, "w1"));
        queue.Complete(queue.Peek(UserA, 10).Select(e => e.Id));

        Assert.Equal(0, Queue().Count);
    }

    [Fact]
    public void The_same_watched_session_is_queued_only_once()
    {
        var queue = Queue();
        queue.Enqueue(UserA, Event(EventActions.Watched, "w1"));
        queue.Enqueue(UserA, Event(EventActions.Watched, "w1"));
        queue.Enqueue(UserB, Event(EventActions.Watched, "w1"));

        Assert.Equal(2, queue.DurableCount);
    }

    [Fact]
    public void The_oldest_watched_events_are_dropped_when_the_queue_is_full()
    {
        var queue = Queue(durable: 3);
        for (var i = 1; i <= 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            queue.Enqueue(UserA, Event(EventActions.Watched, "w" + i));
        }

        var kept = queue.Peek(UserA, 10).Select(e => e.Event.SessionId).ToArray();
        Assert.Equal(["w3", "w4", "w5"], kept);
        Assert.Equal(3, Queue(durable: 3).Count);
    }

    [Fact]
    public void Only_the_newest_progress_of_a_session_is_kept()
    {
        var queue = Queue();
        queue.Enqueue(UserA, Event(EventActions.Progress, "s1"));
        _time.Advance(TimeSpan.FromSeconds(30));
        queue.Enqueue(UserA, Event(EventActions.Progress, "s1"));
        queue.Enqueue(UserA, Event(EventActions.Progress, "s2"));

        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Live_events_are_capped_and_expire()
    {
        var queue = Queue(ephemeral: 3);
        for (var i = 0; i < 6; i++)
        {
            queue.Enqueue(UserA, Event(EventActions.Pause, "s" + i));
        }

        Assert.Equal(3, queue.Count);

        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Empty(queue.PendingUsers());
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Peek_returns_one_users_events_in_the_order_they_happened()
    {
        var queue = Queue();
        queue.Enqueue(UserA, Event(EventActions.Watched, "late"));
        _time.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue(UserB, Event(EventActions.Watched, "other"));
        queue.Enqueue(UserA, Samples.Event(EventActions.Start, "early", _time.GetUtcNow() - TimeSpan.FromMinutes(1)));

        var order = queue.Peek(UserA, 10).Select(e => e.Event.SessionId).ToArray();

        Assert.Equal(["early", "late"], order);
        Assert.Single(queue.Peek(UserA, 1));
    }

    [Fact]
    public void Events_with_the_same_timestamp_keep_the_order_they_were_queued_in_even_across_a_restart()
    {
        var queue = Queue();
        queue.Enqueue(UserA, Event(EventActions.Watched, "first"));
        queue.Enqueue(UserA, Event(EventActions.Stop, "second"));
        queue.Enqueue(UserA, Event(EventActions.Watched, "third"));

        Assert.Equal(["first", "second", "third"], queue.Peek(UserA, 10).Select(e => e.Event.SessionId).ToArray());

        var restarted = Queue();
        restarted.Enqueue(UserA, Event(EventActions.Pause, "fourth"));
        restarted.Enqueue(UserA, Event(EventActions.Watched, "fifth"));
        Assert.Equal(["first", "third", "fourth", "fifth"], restarted.Peek(UserA, 10).Select(e => e.Event.SessionId).ToArray());
    }

    [Fact]
    public void Dropping_a_user_removes_only_that_users_events()
    {
        var queue = Queue();
        queue.Enqueue(UserA, Event(EventActions.Watched, "a"));
        queue.Enqueue(UserA, Event(EventActions.Pause, "a2"));
        queue.Enqueue(UserB, Event(EventActions.Watched, "b"));

        Assert.Equal(2, queue.DropUser(UserA));

        Assert.Equal([UserB], queue.PendingUsers());
        Assert.Equal(1, Queue().Count);
    }

    [Fact]
    public void A_corrupt_queue_file_is_set_aside_and_the_queue_starts_empty()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "pending-watched.json"), "{ not json");

        var queue = Queue();

        Assert.Equal(0, queue.Count);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "pending-watched.json.corrupt")));
        queue.Enqueue(UserA, Event(EventActions.Watched, "w"));
        Assert.Equal(1, Queue().Count);
    }
}

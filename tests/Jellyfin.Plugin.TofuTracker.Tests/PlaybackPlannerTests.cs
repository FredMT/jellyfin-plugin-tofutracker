using Jellyfin.Plugin.TofuTracker.Core;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class PlaybackPlannerTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly PlayKey Key = new(User, "device-session", "item-1");
    private const long Minute = 60L * TimeSpan.TicksPerSecond;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-02T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private PlaybackPlanner Planner() => new(_time);

    private static readonly Func<ScrobbleItem?> Episode = () => Samples.BreakingBadPilot();

    [Fact]
    public void Start_emits_start_with_the_play_session_id_and_item()
    {
        var e = Planner().OnStart(Key, "play-1", 0, 45 * Minute, false, Episode);

        Assert.NotNull(e);
        Assert.Equal(EventActions.Start, e.Action);
        Assert.Equal("play-1", e.SessionId);
        Assert.Null(e.PositionMs);
        Assert.Equal(45 * 60_000, e.DurationMs);
        Assert.False(e.Manual);
        Assert.Equal(Samples.BreakingBadPilot(), e.Item);
        Assert.Equal(_time.GetUtcNow(), e.OccurredAt);
    }

    [Fact]
    public void Progress_is_sent_at_most_every_30_seconds()
    {
        var planner = Planner();
        planner.OnStart(Key, "play-1", 0, 45 * Minute, false, Episode);

        var sent = new List<ScrobbleEvent>();
        for (var second = 1; second <= 100; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            if (planner.OnProgress(Key, "play-1", second * TimeSpan.TicksPerSecond, 45 * Minute, false, Episode) is { } e)
            {
                sent.Add(e);
            }
        }

        // 30 s, 60 s, 90 s after the start.
        Assert.Equal(3, sent.Count);
        Assert.All(sent, e => Assert.Equal(EventActions.Progress, e.Action));
        Assert.All(sent, e => Assert.Equal("play-1", e.SessionId));
        Assert.Equal([30_000L, 60_000L, 90_000L], sent.Select(e => e.PositionMs!.Value).ToArray());
    }

    [Fact]
    public void Item_is_read_once_per_play_not_on_every_tick()
    {
        var planner = Planner();
        var reads = 0;
        ScrobbleItem? Read()
        {
            reads++;
            return Samples.Matrix();
        }

        planner.OnStart(Key, "p", 0, Minute, false, Read);
        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            planner.OnProgress(Key, "p", i * TimeSpan.TicksPerSecond, Minute, false, Read);
        }

        Assert.Equal(1, reads);
    }

    [Fact]
    public void Pause_and_resume_are_sent_immediately_and_paused_ticks_are_not_repeated()
    {
        var planner = Planner();
        planner.OnStart(Key, "p", 0, 45 * Minute, false, Episode);

        _time.Advance(TimeSpan.FromSeconds(5));
        var pause = planner.OnProgress(Key, "p", 5 * TimeSpan.TicksPerSecond, 45 * Minute, true, Episode);
        Assert.Equal(EventActions.Pause, pause?.Action);

        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            Assert.Null(planner.OnProgress(Key, "p", 5 * TimeSpan.TicksPerSecond, 45 * Minute, true, Episode));
        }

        _time.Advance(TimeSpan.FromSeconds(1));
        var resume = planner.OnProgress(Key, "p", 5 * TimeSpan.TicksPerSecond, 45 * Minute, false, Episode);
        Assert.Equal(EventActions.Progress, resume?.Action);
    }

    [Fact]
    public void Stop_below_completion_is_a_stop_event_with_the_position()
    {
        var planner = Planner();
        planner.OnStart(Key, "p", 0, 45 * Minute, false, Episode);
        _time.Advance(TimeSpan.FromMinutes(10));

        var e = planner.OnStop(Key, "p", 10 * Minute, 45 * Minute, false, Episode);

        Assert.Equal(EventActions.Stop, e?.Action);
        Assert.Equal("p", e?.SessionId);
        Assert.Equal(600_000, e?.PositionMs);
    }

    [Fact]
    public void Stop_played_to_completion_is_a_watched_event_in_the_same_session()
    {
        var planner = Planner();
        var start = planner.OnStart(Key, "p", 0, 45 * Minute, false, Episode);
        _time.Advance(TimeSpan.FromMinutes(44));

        var e = planner.OnStop(Key, "p", 44 * Minute, 45 * Minute, true, Episode);

        Assert.Equal(EventActions.Watched, e?.Action);
        Assert.Equal(start?.SessionId, e?.SessionId);
        Assert.False(e?.Manual);
    }

    [Fact]
    public void A_stop_reported_twice_ends_the_play_once()
    {
        var planner = Planner();
        planner.OnStart(Key, "play-1", 0, 45 * Minute, false, Episode);
        _time.Advance(TimeSpan.FromMinutes(44));

        var first = planner.OnStop(Key, "play-1", 45 * Minute, 45 * Minute, true, Episode);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        var repeat = planner.OnStop(Key, null, 45 * Minute, 45 * Minute, true, Episode);
        var lateTick = planner.OnProgress(Key, "play-1", 45 * Minute, 45 * Minute, false, Episode);

        Assert.Equal(EventActions.Watched, first?.Action);
        Assert.Null(repeat);
        Assert.Null(lateTick);

        // A real replay right after still starts a new play.
        var again = planner.OnStart(Key, "play-2", 0, 45 * Minute, false, Episode);
        Assert.Equal(EventActions.Start, again?.Action);
        Assert.Equal("play-2", again?.SessionId);
    }

    [Fact]
    public void A_play_session_ends_with_stop_so_the_next_play_of_the_same_item_starts_fresh()
    {
        var planner = Planner();
        planner.OnStart(Key, "p1", 0, Minute, false, Episode);
        planner.OnStop(Key, "p1", Minute, Minute, true, Episode);

        var again = planner.OnStart(Key, "p2", 0, Minute, false, Episode);

        Assert.Equal(EventActions.Start, again?.Action);
        Assert.Equal("p2", again?.SessionId);
    }

    [Fact]
    public void Progress_for_a_play_that_never_started_is_still_reported_and_then_throttled()
    {
        var planner = Planner();

        var first = planner.OnProgress(Key, "p", 10 * TimeSpan.TicksPerSecond, Minute, false, Episode);
        _time.Advance(TimeSpan.FromSeconds(10));
        var second = planner.OnProgress(Key, "p", 20 * TimeSpan.TicksPerSecond, Minute, false, Episode);

        Assert.Equal(EventActions.Progress, first?.Action);
        Assert.Null(second);
    }

    [Fact]
    public void A_start_that_arrives_after_its_first_progress_or_twice_is_not_reported_again()
    {
        var planner = Planner();

        var first = planner.OnProgress(Key, "p", TimeSpan.TicksPerSecond, Minute, false, Episode);
        var lateStart = planner.OnStart(Key, "p", 0, Minute, false, Episode);
        var again = planner.OnStart(Key, "p", 0, Minute, false, Episode);

        Assert.Equal(EventActions.Progress, first?.Action);
        Assert.Null(lateStart);
        Assert.Null(again);
    }

    [Fact]
    public void Same_item_replayed_without_a_stop_gets_a_new_session_and_a_start()
    {
        var planner = Planner();
        planner.OnStart(Key, "p1", 0, Minute, false, Episode);

        var e = planner.OnProgress(Key, "p2", 0, Minute, false, Episode);

        Assert.Equal(EventActions.Start, e?.Action);
        Assert.Equal("p2", e?.SessionId);
    }

    [Fact]
    public void Missing_play_session_id_gets_one_random_id_that_stays_stable_for_the_play()
    {
        var planner = Planner();
        var start = planner.OnStart(Key, null, 0, Minute, false, Episode);
        _time.Advance(TimeSpan.FromSeconds(31));
        var progress = planner.OnProgress(Key, null, TimeSpan.TicksPerSecond, Minute, false, Episode);
        var stop = planner.OnStop(Key, "late-id", Minute, Minute, true, Episode);

        Assert.False(string.IsNullOrWhiteSpace(start?.SessionId));
        Assert.Equal(start?.SessionId, progress?.SessionId);
        Assert.Equal(start?.SessionId, stop?.SessionId);

        var next = planner.OnStart(Key, null, 0, Minute, false, Episode);
        Assert.NotEqual(start?.SessionId, next?.SessionId);
    }

    [Fact]
    public void Progress_with_no_id_before_a_start_with_one_is_one_play_with_the_progress_session_id()
    {
        // As seen on the wire: Jellyfin delivered the progress tick (position 52 ms) before the start callback, and the
        // two carried different ids, which left an orphan session on the scrobbler.
        var planner = Planner();

        var progress = planner.OnProgress(Key, null, 52 * TimeSpan.TicksPerMillisecond, Minute, false, Episode);
        _time.Advance(TimeSpan.FromMilliseconds(40));
        var start = planner.OnStart(Key, "7dd49976-0000-0000-0000-000000000000", 0, Minute, false, Episode);

        Assert.Equal(EventActions.Progress, progress?.Action);
        Assert.Equal(52, progress?.PositionMs);
        Assert.False(string.IsNullOrWhiteSpace(progress?.SessionId));
        Assert.Null(start);

        _time.Advance(TimeSpan.FromSeconds(31));
        var later = planner.OnProgress(Key, "7dd49976-0000-0000-0000-000000000000", 31 * TimeSpan.TicksPerSecond, Minute, false, Episode);
        var stop = planner.OnStop(Key, "7dd49976-0000-0000-0000-000000000000", 40 * TimeSpan.TicksPerSecond, Minute, true, Episode);
        Assert.Equal(progress?.SessionId, later?.SessionId);
        Assert.Equal(progress?.SessionId, stop?.SessionId);
        Assert.Equal(EventActions.Watched, stop?.Action);
    }

    [Fact]
    public void Progress_and_start_with_different_ids_a_moment_apart_are_one_play()
    {
        var planner = Planner();

        var progress = planner.OnProgress(Key, "a79df3db", 52 * TimeSpan.TicksPerMillisecond, Minute, false, Episode);
        _time.Advance(TimeSpan.FromMilliseconds(40));
        var start = planner.OnStart(Key, "7dd49976", 0, Minute, false, Episode);
        Assert.Null(start);

        // Either id keeps meaning this play from now on.
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(progress?.SessionId, planner.OnProgress(Key, "7dd49976", TimeSpan.TicksPerSecond, Minute, false, Episode)?.SessionId);
        Assert.Equal(progress?.SessionId, planner.OnStop(Key, "a79df3db", Minute, Minute, false, Episode)?.SessionId);
        Assert.Equal("a79df3db", progress?.SessionId);
    }

    [Fact]
    public void Progress_with_an_id_before_a_start_without_one_is_one_play()
    {
        var planner = Planner();

        var progress = planner.OnProgress(Key, "p", TimeSpan.TicksPerSecond, Minute, false, Episode);
        var start = planner.OnStart(Key, null, 0, Minute, false, Episode);

        Assert.NotNull(progress);
        Assert.Null(start);
        Assert.Equal("p", planner.OnStop(Key, "p", Minute, Minute, true, Episode)?.SessionId);
    }

    [Fact]
    public void A_start_with_a_new_id_long_after_a_progress_born_play_is_a_new_play()
    {
        var planner = Planner();
        var first = planner.OnProgress(Key, "p1", TimeSpan.TicksPerSecond, Minute, false, Episode);

        _time.Advance(TimeSpan.FromMinutes(5));
        var again = planner.OnStart(Key, "p2", 0, Minute, false, Episode);

        Assert.Equal("p1", first?.SessionId);
        Assert.Equal(EventActions.Start, again?.Action);
        Assert.Equal("p2", again?.SessionId);
    }

    [Fact]
    public void A_second_start_with_a_new_id_after_a_real_start_is_a_new_play()
    {
        var planner = Planner();
        planner.OnStart(Key, "p1", 0, Minute, false, Episode);

        _time.Advance(TimeSpan.FromMilliseconds(40));
        var again = planner.OnStart(Key, "p2", 0, Minute, false, Episode);

        Assert.Equal("p2", again?.SessionId);
    }

    [Fact]
    public void A_play_that_began_without_an_id_keeps_its_session_id_when_a_later_start_brings_one()
    {
        var planner = Planner();
        var first = planner.OnStart(Key, null, 0, Minute, false, Episode);

        _time.Advance(TimeSpan.FromSeconds(31));
        var duplicate = planner.OnStart(Key, "late-id", 0, Minute, false, Episode);
        var progress = planner.OnProgress(Key, "late-id", TimeSpan.TicksPerSecond, Minute, false, Episode);

        Assert.Null(duplicate);
        Assert.Equal(first?.SessionId, progress?.SessionId);
    }

    [Fact]
    public void Overlong_session_ids_are_hashed_to_fit_the_limit()
    {
        var id = PlaybackPlanner.SessionIdFor(new string('a', 500));

        Assert.True(id.Length <= PlaybackPlanner.MaxSessionIdLength);
        Assert.StartsWith("h:", id, StringComparison.Ordinal);
        Assert.Equal(id, PlaybackPlanner.SessionIdFor(new string('a', 500)));
        Assert.Equal("short", PlaybackPlanner.SessionIdFor("short"));
    }

    [Fact]
    public void Items_without_ids_produce_no_events_at_all()
    {
        var planner = Planner();
        ScrobbleItem? Nothing() => null;

        Assert.Null(planner.OnStart(Key, "p", 0, Minute, false, Nothing));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(planner.OnProgress(Key, "p", 0, Minute, false, Nothing));
        Assert.Null(planner.OnStop(Key, "p", Minute, Minute, true, Nothing));
    }

    [Fact]
    public void Different_users_on_one_device_are_tracked_separately()
    {
        var planner = Planner();
        var other = Key with { UserId = Guid.NewGuid() };

        var a = planner.OnStart(Key, "p", 0, Minute, false, Episode);
        var b = planner.OnStart(other, "p", 0, Minute, false, Episode);
        planner.OnStop(Key, "p", Minute, Minute, true, Episode);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(EventActions.Progress, planner.OnProgress(other, "p", Minute, Minute, false, Episode)?.Action);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Manual_mark_is_a_watched_event_flagged_manual_with_a_day_scoped_session_id()
    {
        var e = Planner().OnManualPlayed(User, Episode);

        Assert.NotNull(e);
        Assert.Equal(EventActions.Watched, e.Action);
        Assert.True(e.Manual);
        Assert.Null(e.PositionMs);
        Assert.Equal($"manual:{ItemMapper.ContentKey(Samples.BreakingBadPilot())}:2026-10-02", e.SessionId);
    }

    [Fact]
    public void Manual_mark_twice_is_sent_once_and_again_the_next_day()
    {
        var planner = Planner();

        Assert.NotNull(planner.OnManualPlayed(User, Episode));
        Assert.Null(planner.OnManualPlayed(User, Episode));

        _time.Advance(TimeSpan.FromHours(24));
        var nextDay = planner.OnManualPlayed(User, Episode);
        Assert.NotNull(nextDay);
        Assert.EndsWith(":2026-10-03", nextDay.SessionId, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_mark_during_or_right_after_a_real_play_is_ignored()
    {
        var planner = Planner();
        planner.OnStart(Key, "p", 0, Minute, false, Episode);

        // Marked played while playing, e.g. a client that also calls "mark played" at the end.
        Assert.Null(planner.OnManualPlayed(User, Episode));

        planner.OnStop(Key, "p", Minute, Minute, true, Episode);
        _time.Advance(TimeSpan.FromMinutes(2));
        // Jellyfin marks the other versions of the video played right after a play (TogglePlayed).
        Assert.Null(planner.OnManualPlayed(User, Episode));

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(planner.OnManualPlayed(User, Episode));
    }

    [Fact]
    public void Manual_mark_of_something_else_is_not_suppressed_by_a_play()
    {
        var planner = Planner();
        planner.OnStart(Key, "p", 0, Minute, false, Episode);

        Assert.NotNull(planner.OnManualPlayed(User, () => Samples.Matrix()));
    }

    [Fact]
    public void Manual_mark_by_another_user_is_not_suppressed()
    {
        var planner = Planner();
        planner.OnStart(Key, "p", 0, Minute, false, Episode);

        Assert.NotNull(planner.OnManualPlayed(Guid.NewGuid(), Episode));
    }

    [Fact]
    public void Manual_mark_of_an_item_without_ids_is_ignored()
    {
        Assert.Null(Planner().OnManualPlayed(User, () => null));
    }

    [Fact]
    public void Stale_play_state_is_forgotten()
    {
        var planner = Planner();
        planner.OnStart(Key, "p", 0, Minute, false, Episode);

        _time.Advance(TimeSpan.FromHours(13));

        // The old play no longer blocks a manual mark, and a stop is treated as a lone event.
        Assert.NotNull(planner.OnManualPlayed(User, Episode));
    }
}

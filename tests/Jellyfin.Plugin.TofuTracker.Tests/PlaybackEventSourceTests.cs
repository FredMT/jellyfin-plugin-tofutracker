using System.Net;
using Jellyfin.Plugin.TofuTracker.Core;
using Jellyfin.Plugin.TofuTracker.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.TofuTracker.Tests;

/// <summary>Drives the hosted service through substitutes of Jellyfin's session and user-data managers, down to the HTTP request.</summary>
[Collection(JellyfinStaticsCollection.Name)]
public sealed class PlaybackEventSourceTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly FakeHandler _http = new();
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly IUserDataManager _userData = Substitute.For<IUserDataManager>();
    private readonly Jellyfin.Database.Implementations.Entities.User _alice = JellyfinFixtures.NewUser("alice");
    private readonly Jellyfin.Database.Implementations.Entities.User _bob = JellyfinFixtures.NewUser("bob");
    private readonly CapturingLoggerFactory _logs = new();
    private SkippedItemLog _skipped = null!;
    private PlaybackEventSource _source = null!;

    public async Task InitializeAsync()
    {
        var links = new LinkStore(_dir.Path, Samples.Logger);
        links.Upsert(Samples.Link(_alice.Id, "alice-token", "alice-tt"));

        var queue = new OutboundQueue(_dir.Path, Samples.Logger, TimeProvider.System);
        var sender = new ScrobbleSender(queue, links, _http.CreateClient, () => "https://scrobble.test", () => Samples.Client, TimeProvider.System, Samples.Logger)
        {
            CoalesceDelay = TimeSpan.Zero,
        };
        var pairing = new PairingCoordinator(_http.CreateClient, () => "https://scrobble.test", () => Samples.Client, links, TimeProvider.System, Samples.Logger);

        _skipped = new SkippedItemLog(TimeProvider.System);
        _source = new PlaybackEventSource(_sessions, _userData, links, new PlaybackPlanner(TimeProvider.System), queue, sender, pairing, _skipped, _logs);
        await _source.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        await _source.StopAsync(default);
        _source.Dispose();
        _dir.Dispose();
    }

    private void Playback(string kind, BaseItem item, Jellyfin.Database.Implementations.Entities.User user, string? playSessionId, long positionTicks = 0, bool paused = false, bool completed = false)
    {
        var args = new PlaybackProgressEventArgs
        {
            Item = item,
            Users = [user],
            PlaySessionId = playSessionId,
            DeviceId = "device-1",
            PlaybackPositionTicks = positionTicks,
            IsPaused = paused,
        };

        switch (kind)
        {
            case "start":
                _sessions.PlaybackStart += Raise.EventWith(_sessions, args);
                break;
            case "progress":
                _sessions.PlaybackProgress += Raise.EventWith(_sessions, args);
                break;
            default:
                _sessions.PlaybackStopped += Raise.EventWith(
                    _sessions,
                    new PlaybackStopEventArgs
                    {
                        Item = item,
                        Users = [user],
                        PlaySessionId = playSessionId,
                        DeviceId = "device-1",
                        PlaybackPositionTicks = positionTicks,
                        PlayedToCompletion = completed,
                    });
                break;
        }
    }

    private void UserDataSaved(BaseItem item, Jellyfin.Database.Implementations.Entities.User user, UserDataSaveReason reason, bool played)
    {
        _userData.UserDataSaved += Raise.EventWith(
            _userData,
            new UserDataSaveEventArgs
            {
                Item = item,
                UserId = user.Id,
                SaveReason = reason,
                Keys = ["k"],
                UserData = new UserItemData { Key = "k", Played = played },
            });
    }

    private async Task<List<System.Text.Json.JsonElement>> EventsAsync(int expected, int settleMs = 150)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var events = Snapshot();
            if (events.Count >= expected)
            {
                // Give a wrongly extra event a moment to show up.
                await Task.Delay(settleMs);
                return Snapshot();
            }

            await Task.Delay(20);
        }

        return Snapshot();
    }

    private List<System.Text.Json.JsonElement> Snapshot()
    {
        lock (_http.Requests)
        {
            return [.. _http.Requests.SelectMany(r => r.Events.EnumerateArray().Select(e => e.Clone()))];
        }
    }

    [Fact]
    public async Task A_completed_movie_play_sends_start_then_watched_with_the_jellyfin_play_session_id()
    {
        var movie = JellyfinFixtures.Matrix();

        Playback("start", movie, _alice, "play-1");
        Playback("stop", movie, _alice, "play-1", positionTicks: 130 * 60 * TimeSpan.TicksPerSecond, completed: true);

        var events = await EventsAsync(2);
        Assert.Equal(["start", "watched"], events.Select(e => e.GetProperty("action").GetString()!).ToArray());
        Assert.All(events, e => Assert.Equal("play-1", e.GetProperty("sessionId").GetString()));
        var item = events[1].GetProperty("item");
        Assert.Equal("movie", item.GetProperty("kind").GetString());
        Assert.Equal(603, item.GetProperty("ids").GetProperty("tmdb").GetInt64());
        Assert.Equal("tt0133093", item.GetProperty("ids").GetProperty("imdb").GetString());
        Assert.Equal(130 * 60_000, events[1].GetProperty("positionMs").GetInt64());
        Assert.Equal(136 * 60_000, events[1].GetProperty("durationMs").GetInt64());
        Assert.False(events[1].GetProperty("manual").GetBoolean());
        Assert.All(_http.Requests, r => Assert.Equal("Bearer alice-token", r.Authorization));
    }

    [Fact]
    public async Task An_episode_play_carries_series_level_ids_and_the_episode_ids()
    {
        var episode = JellyfinFixtures.BreakingBadPilot();

        Playback("start", episode, _alice, "play-2");

        var events = await EventsAsync(1);
        var item = Assert.Single(events).GetProperty("item");
        Assert.Equal("episode", item.GetProperty("kind").GetString());
        Assert.Equal("Breaking Bad - Pilot", item.GetProperty("title").GetString());
        Assert.Equal(81189, item.GetProperty("ids").GetProperty("tvdb").GetInt64());
        Assert.Equal(1396, item.GetProperty("ids").GetProperty("tmdb").GetInt64());
        Assert.Equal(349232, item.GetProperty("episodeIds").GetProperty("tvdb").GetInt64());
        Assert.Equal(1, item.GetProperty("season").GetInt32());
        Assert.Equal(1, item.GetProperty("episode").GetInt32());
        Assert.Equal("tvdb", item.GetProperty("numbering").GetString());
    }

    [Fact]
    public async Task A_stop_short_of_completion_sends_stop()
    {
        var movie = JellyfinFixtures.Matrix();

        Playback("start", movie, _alice, "play-3");
        Playback("stop", movie, _alice, "play-3", positionTicks: 20 * 60 * TimeSpan.TicksPerSecond, completed: false);

        var events = await EventsAsync(2);
        Assert.Equal(["start", "stop"], events.Select(e => e.GetProperty("action").GetString()!).ToArray());
    }

    [Fact]
    public async Task Progress_ticks_inside_the_throttle_window_are_not_sent()
    {
        var movie = JellyfinFixtures.Matrix();

        Playback("start", movie, _alice, "play-4");
        for (var i = 1; i <= 5; i++)
        {
            Playback("progress", movie, _alice, "play-4", positionTicks: i * 10 * TimeSpan.TicksPerSecond);
        }

        var events = await EventsAsync(1, 300);
        Assert.Equal(["start"], events.Select(e => e.GetProperty("action").GetString()!).ToArray());
    }

    [Fact]
    public async Task Pausing_is_sent_right_away()
    {
        var movie = JellyfinFixtures.Matrix();

        Playback("start", movie, _alice, "play-5");
        Playback("progress", movie, _alice, "play-5", positionTicks: 100 * TimeSpan.TicksPerSecond, paused: true);

        var events = await EventsAsync(2);
        Assert.Equal(["start", "pause"], events.Select(e => e.GetProperty("action").GetString()!).ToArray());
    }

    [Fact]
    public async Task Marking_an_item_played_sends_a_manual_watched_event()
    {
        UserDataSaved(JellyfinFixtures.BreakingBadPilot(), _alice, UserDataSaveReason.TogglePlayed, played: true);

        var events = await EventsAsync(1);
        var e = Assert.Single(events);
        Assert.Equal("watched", e.GetProperty("action").GetString());
        Assert.True(e.GetProperty("manual").GetBoolean());
        Assert.StartsWith("manual:e:", e.GetProperty("sessionId").GetString(), StringComparison.Ordinal);
        Assert.Equal(81189, e.GetProperty("item").GetProperty("ids").GetProperty("tvdb").GetInt64());
        Assert.False(e.TryGetProperty("positionMs", out _));
    }

    [Theory]
    [InlineData(UserDataSaveReason.PlaybackStart, true)]
    [InlineData(UserDataSaveReason.PlaybackProgress, true)]
    [InlineData(UserDataSaveReason.PlaybackFinished, true)]
    [InlineData(UserDataSaveReason.Import, true)]
    [InlineData(UserDataSaveReason.UpdateUserRating, true)]
    [InlineData(UserDataSaveReason.UpdateUserData, true)]
    [InlineData(UserDataSaveReason.TogglePlayed, false)]
    public async Task Only_marking_played_by_hand_counts_as_a_manual_sync(UserDataSaveReason reason, bool played)
    {
        UserDataSaved(JellyfinFixtures.Matrix(), _alice, reason, played);

        Assert.Empty(await EventsAsync(0, 300));
    }

    [Fact]
    public async Task A_mark_played_right_after_the_real_play_is_not_sent_twice()
    {
        var movie = JellyfinFixtures.Matrix();
        Playback("start", movie, _alice, "play-6");
        Playback("stop", movie, _alice, "play-6", positionTicks: 130 * 60 * TimeSpan.TicksPerSecond, completed: true);

        // Jellyfin marks alternate versions of the movie played after a play.
        UserDataSaved(JellyfinFixtures.Matrix(), _alice, UserDataSaveReason.TogglePlayed, played: true);

        var events = await EventsAsync(2, 300);
        Assert.Equal(["start", "watched"], events.Select(e => e.GetProperty("action").GetString()!).ToArray());
    }

    [Fact]
    public async Task Users_that_are_not_linked_send_nothing()
    {
        var movie = JellyfinFixtures.Matrix();

        Playback("start", movie, _bob, "play-7");
        Playback("stop", movie, _bob, "play-7", completed: true);
        UserDataSaved(movie, _bob, UserDataSaveReason.TogglePlayed, played: true);

        Assert.Empty(await EventsAsync(0, 300));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Items_that_are_not_movies_or_episodes_send_nothing()
    {
        var song = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Name = "Song" };
        song.ProviderIds["Tmdb"] = "1";

        Playback("start", song, _alice, "play-8");
        UserDataSaved(song, _alice, UserDataSaveReason.TogglePlayed, played: true);

        Assert.Empty(await EventsAsync(0, 300));
    }

    [Fact]
    public async Task Titles_without_provider_ids_send_nothing()
    {
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Home video" };

        Playback("start", movie, _alice, "play-9");
        Playback("stop", movie, _alice, "play-9", completed: true);

        Assert.Empty(await EventsAsync(0, 300));
    }

    [Fact]
    public async Task A_skipped_title_is_logged_once_at_information_and_listed_for_the_admin_page()
    {
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Home video" };

        // A play with its progress ticks, then a replay and a manual mark: the log line comes once, not per tick.
        Playback("start", movie, _alice, "play-a");
        for (var i = 1; i <= 5; i++)
        {
            Playback("progress", movie, _alice, "play-a", positionTicks: i * TimeSpan.TicksPerSecond);
        }

        Playback("stop", movie, _alice, "play-a", completed: false);
        Playback("start", movie, _alice, "play-b");
        UserDataSaved(movie, _alice, UserDataSaveReason.TogglePlayed, played: true);
        Assert.Empty(await EventsAsync(0, 400));

        var lines = _logs.Snapshot().Where(e => e.Message.Contains("Home video", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("refresh or identify", line.Message, StringComparison.OrdinalIgnoreCase);

        var listed = Assert.Single(_skipped.Recent());
        Assert.Equal("Home video", listed.Name);
        Assert.Contains("provider ids", listed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_skipped_title_gets_its_own_log_line()
    {
        var first = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Home video" };
        var second = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Holiday clip" };

        Playback("start", first, _alice, "p1");
        Playback("start", second, _alice, "p2");
        await EventsAsync(0, 400);

        Assert.Equal(2, _logs.Snapshot().Count(e => e.Level == LogLevel.Information && e.Message.Contains("is not sending", StringComparison.Ordinal)));
        Assert.Equal(["Holiday clip", "Home video"], _skipped.Recent().Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task A_progress_tick_that_beats_its_start_and_carries_no_id_is_still_one_session()
    {
        var movie = JellyfinFixtures.Matrix();

        Playback("progress", movie, _alice, null, positionTicks: 52 * TimeSpan.TicksPerMillisecond);
        Playback("start", movie, _alice, "7dd49976");
        Playback("stop", movie, _alice, "7dd49976", positionTicks: 130 * 60 * TimeSpan.TicksPerSecond, completed: true);

        var events = await EventsAsync(2);
        Assert.Equal(["progress", "watched"], events.Select(e => e.GetProperty("action").GetString()!).ToArray());
        Assert.Single(events.Select(e => e.GetProperty("sessionId").GetString()).Distinct());
        Assert.Equal(52, events[0].GetProperty("positionMs").GetInt64());
    }

    [Fact]
    public async Task A_progress_tick_that_beats_its_start_with_another_id_is_still_one_session()
    {
        var movie = JellyfinFixtures.Matrix();

        Playback("progress", movie, _alice, "a79df3db", positionTicks: 52 * TimeSpan.TicksPerMillisecond);
        Playback("start", movie, _alice, "7dd49976");
        Playback("stop", movie, _alice, "7dd49976", positionTicks: 20 * 60 * TimeSpan.TicksPerSecond, completed: false);

        var events = await EventsAsync(2);
        Assert.Equal(["progress", "stop"], events.Select(e => e.GetProperty("action").GetString()!).ToArray());
        Assert.All(events, e => Assert.Equal("a79df3db", e.GetProperty("sessionId").GetString()));
    }

    [Fact]
    public async Task A_server_error_does_not_break_event_handling()
    {
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Playback("start", JellyfinFixtures.Matrix(), _alice, "play-10");
        await EventsAsync(1);

        // Handlers keep working (no exception reached Jellyfin) and the next event is accepted for sending.
        Playback("progress", JellyfinFixtures.Matrix(), _alice, "play-10", paused: true);
        await Task.Delay(100);
        Assert.NotEmpty(_http.Requests);
    }
}

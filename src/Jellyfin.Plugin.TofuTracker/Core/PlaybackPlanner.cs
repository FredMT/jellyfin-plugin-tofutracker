using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>Identifies one playback of one item by one user on one Jellyfin session (a device).</summary>
public readonly record struct PlayKey(Guid UserId, string DeviceSessionId, string ItemId);

/// <summary>
/// Turns Jellyfin's playback callbacks into contract events: one session id per play, progress at most
/// every <see cref="ProgressInterval"/>, pause and resume as they happen, and a manual "mark played" that
/// is skipped when a real play of the same thing just happened.
/// </summary>
public sealed class PlaybackPlanner
{
    public const int MaxSessionIdLength = 200;

    private static readonly TimeSpan StateLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan RecentLifetime = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<PlayKey, PlayState> _active = [];
    private readonly Dictionary<(Guid UserId, string ContentKey), DateTimeOffset> _recentPlays = [];
    private readonly Dictionary<(Guid UserId, string SessionId), DateTimeOffset> _recentManual = [];

    public PlaybackPlanner(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Minimum gap between two progress events of one play.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after a real play (or during it) a manual "mark played" of the same content is ignored.</summary>
    public TimeSpan ManualSuppression { get; init; } = TimeSpan.FromMinutes(10);

    public ScrobbleEvent? OnStart(PlayKey key, string? playSessionId, long? positionTicks, long? runtimeTicks, bool isPaused, Func<ScrobbleItem?> readItem)
    {
        ArgumentNullException.ThrowIfNull(readItem);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);

            // Jellyfin queues the start callback but runs progress inline, so a progress tick can win the race,
            // and clients sometimes report a start twice. Either way this play is already being tracked.
            if (!string.IsNullOrWhiteSpace(playSessionId)
                && _active.TryGetValue(key, out var existing)
                && string.Equals(existing.RawPlaySessionId, playSessionId, StringComparison.Ordinal))
            {
                return null;
            }

            var state = Begin(key, playSessionId, readItem, now);
            if (state.Item is null)
            {
                return null;
            }

            state.Paused = isPaused;
            state.LastSent = now;
            return Build(isPaused ? EventActions.Pause : EventActions.Start, state, positionTicks, runtimeTicks, now);
        }
    }

    public ScrobbleEvent? OnProgress(PlayKey key, string? playSessionId, long? positionTicks, long? runtimeTicks, bool isPaused, Func<ScrobbleItem?> readItem)
    {
        ArgumentNullException.ThrowIfNull(readItem);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);

            if (!_active.TryGetValue(key, out var state) || IsNewPlay(state, playSessionId))
            {
                // Progress for a play we never saw start (server restarted mid-play, or the same item replayed).
                var fresh = state is null;
                state = Begin(key, playSessionId, readItem, now);
                if (state.Item is null)
                {
                    return null;
                }

                state.Paused = isPaused;
                state.LastSent = now;
                return Build(
                    isPaused ? EventActions.Pause : fresh ? EventActions.Progress : EventActions.Start,
                    state,
                    positionTicks,
                    runtimeTicks,
                    now);
            }

            state.LastSeen = now;
            if (state.Item is null)
            {
                return null;
            }

            Touch(key.UserId, state.ContentKey, now);

            if (isPaused != state.Paused)
            {
                state.Paused = isPaused;
                state.LastSent = now;
                return Build(isPaused ? EventActions.Pause : EventActions.Progress, state, positionTicks, runtimeTicks, now);
            }

            if (isPaused || now - state.LastSent < ProgressInterval)
            {
                return null;
            }

            state.LastSent = now;
            return Build(EventActions.Progress, state, positionTicks, runtimeTicks, now);
        }
    }

    public ScrobbleEvent? OnStop(PlayKey key, string? playSessionId, long? positionTicks, long? runtimeTicks, bool playedToCompletion, Func<ScrobbleItem?> readItem)
    {
        ArgumentNullException.ThrowIfNull(readItem);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);

            PlayState state;
            if (_active.Remove(key, out var existing) && !IsNewPlay(existing, playSessionId))
            {
                state = existing;
            }
            else
            {
                state = Begin(key, playSessionId, readItem, now);
                _active.Remove(key);
            }

            if (state.Item is null)
            {
                return null;
            }

            Touch(key.UserId, state.ContentKey, now);

            // Jellyfin's own "played to completion" and a stop short of it are mutually exclusive terminal
            // events: the scrobbler's engine takes either `stop` or `watched` as the end of a session.
            return Build(playedToCompletion ? EventActions.Watched : EventActions.Stop, state, positionTicks, runtimeTicks, now);
        }
    }

    /// <summary>
    /// A "mark as played" the user did by hand. Returns null when the same content was played (or is playing)
    /// just now, which also covers Jellyfin marking alternate versions of a video played after a real play.
    /// </summary>
    public ScrobbleEvent? OnManualPlayed(Guid userId, Func<ScrobbleItem?> readItem)
    {
        ArgumentNullException.ThrowIfNull(readItem);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);

            var item = readItem();
            if (item is null)
            {
                return null;
            }

            var contentKey = ItemMapper.ContentKey(item);
            if (_active.Any(p => p.Key.UserId == userId && p.Value.ContentKey == contentKey)
                || (_recentPlays.TryGetValue((userId, contentKey), out var lastPlay) && now - lastPlay < ManualSuppression))
            {
                return null;
            }

            var sessionId = ManualSessionId(contentKey, now);
            if (_recentManual.TryGetValue((userId, sessionId), out var lastManual) && now - lastManual < ManualSuppression)
            {
                return null;
            }

            _recentManual[(userId, sessionId)] = now;
            return new ScrobbleEvent(EventActions.Watched, now, sessionId, null, null, true, item);
        }
    }

    /// <summary>The id a manual mark gets: stable for one item and day, so the scrobbler dedupes repeats.</summary>
    public static string ManualSessionId(string contentKey, DateTimeOffset at)
    {
        var day = at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Clip($"manual:{contentKey}:{day}");
    }

    /// <summary>The play session id: Jellyfin's own when there is one, else a random one for this play.</summary>
    public static string SessionIdFor(string? playSessionId)
    {
        return string.IsNullOrWhiteSpace(playSessionId) ? Guid.NewGuid().ToString("N") : Clip(playSessionId.Trim());
    }

    private static string Clip(string value)
    {
        if (value.Length <= MaxSessionIdLength)
        {
            return value;
        }

        return "h:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool IsNewPlay(PlayState state, string? playSessionId)
    {
        // A play that started without a PlaySessionId adopts whichever id later events carry.
        return !string.IsNullOrWhiteSpace(playSessionId)
            && state.RawPlaySessionId is not null
            && !string.Equals(state.RawPlaySessionId, playSessionId, StringComparison.Ordinal);
    }

    private PlayState Begin(PlayKey key, string? playSessionId, Func<ScrobbleItem?> readItem, DateTimeOffset now)
    {
        var item = readItem();
        var state = new PlayState
        {
            RawPlaySessionId = string.IsNullOrWhiteSpace(playSessionId) ? null : playSessionId,
            SessionId = SessionIdFor(playSessionId),
            Item = item,
            ContentKey = item is null ? string.Empty : ItemMapper.ContentKey(item),
            LastSeen = now,
            LastSent = now,
        };
        _active[key] = state;
        if (item is not null)
        {
            Touch(key.UserId, state.ContentKey, now);
        }

        return state;
    }

    private void Touch(Guid userId, string contentKey, DateTimeOffset now)
    {
        _recentPlays[(userId, contentKey)] = now;
    }

    private static ScrobbleEvent Build(string action, PlayState state, long? positionTicks, long? runtimeTicks, DateTimeOffset now)
    {
        return new ScrobbleEvent(
            action,
            now,
            state.SessionId,
            TicksToMs(positionTicks),
            TicksToMs(runtimeTicks),
            false,
            state.Item!);
    }

    private static long? TicksToMs(long? ticks)
    {
        return ticks is > 0 ? ticks.Value / TimeSpan.TicksPerMillisecond : null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _active.Where(p => now - p.Value.LastSeen > StateLifetime).Select(p => p.Key).ToList())
        {
            _active.Remove(key);
        }

        foreach (var key in _recentPlays.Where(p => now - p.Value > RecentLifetime).Select(p => p.Key).ToList())
        {
            _recentPlays.Remove(key);
        }

        foreach (var key in _recentManual.Where(p => now - p.Value > RecentLifetime).Select(p => p.Key).ToList())
        {
            _recentManual.Remove(key);
        }
    }

    private sealed class PlayState
    {
        public string? RawPlaySessionId { get; init; }

        public required string SessionId { get; init; }

        public ScrobbleItem? Item { get; init; }

        public required string ContentKey { get; init; }

        public DateTimeOffset LastSeen { get; set; }

        public DateTimeOffset LastSent { get; set; }

        public bool Paused { get; set; }
    }
}

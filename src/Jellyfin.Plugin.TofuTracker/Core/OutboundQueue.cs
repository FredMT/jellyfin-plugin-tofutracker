using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>An event waiting to be sent with the token of one Jellyfin user.</summary>
public sealed record QueuedEvent(Guid Id, Guid UserId, DateTimeOffset EnqueuedAt, ScrobbleEvent Event, long Sequence = 0)
{
    /// <summary>A finished watch must not be lost; everything else is only worth sending while it is fresh.</summary>
    public bool IsDurable => string.Equals(Event.Action, EventActions.Watched, StringComparison.Ordinal);
}

/// <summary>
/// The events not yet accepted by the scrobbler. <c>watched</c> events are kept on disk (bounded, oldest
/// dropped first) so they survive a Jellyfin restart. Start, progress, pause and stop events are memory-only,
/// capped, and dropped once stale.
/// </summary>
public sealed class OutboundQueue
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<QueuedEvent> _durable = [];
    private readonly List<QueuedEvent> _ephemeral = [];
    private long _sequence;

    public OutboundQueue(string directory, ILogger logger, TimeProvider time)
    {
        _path = Path.Combine(directory, "pending-watched.json");
        _logger = logger;
        _time = time;
        Load();
    }

    /// <summary>Most finished watches kept on disk.</summary>
    public int DurableCapacity { get; init; } = 1000;

    /// <summary>Most live events kept in memory.</summary>
    public int EphemeralCapacity { get; init; } = 200;

    /// <summary>Live events older than this are dropped instead of sent late.</summary>
    public TimeSpan EphemeralMaxAge { get; init; } = TimeSpan.FromMinutes(5);

    public int DurableCount
    {
        get
        {
            lock (_gate)
            {
                return _durable.Count;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _durable.Count + _ephemeral.Count;
            }
        }
    }

    public void Enqueue(Guid userId, ScrobbleEvent scrobbleEvent)
    {
        ArgumentNullException.ThrowIfNull(scrobbleEvent);
        lock (_gate)
        {
            var entry = new QueuedEvent(Guid.NewGuid(), userId, _time.GetUtcNow(), scrobbleEvent, ++_sequence);
            if (entry.IsDurable)
            {
                // The same manual mark twice (or a replayed stop) is one watch to the scrobbler; don't pile it up.
                if (_durable.Any(e => e.UserId == userId && string.Equals(e.Event.SessionId, scrobbleEvent.SessionId, StringComparison.Ordinal)))
                {
                    return;
                }

                _durable.Add(entry);
                if (_durable.Count > DurableCapacity)
                {
                    var overflow = _durable.Count - DurableCapacity;
                    _logger.LogWarning("TofuTracker queue is full; dropping the {Count} oldest unsent watches.", overflow);
                    _durable.RemoveRange(0, overflow);
                }

                SaveDurable();
                return;
            }

            if (string.Equals(scrobbleEvent.Action, EventActions.Progress, StringComparison.Ordinal))
            {
                // Only the newest progress of a play is worth sending.
                _ephemeral.RemoveAll(e => e.UserId == userId
                    && string.Equals(e.Event.Action, EventActions.Progress, StringComparison.Ordinal)
                    && string.Equals(e.Event.SessionId, scrobbleEvent.SessionId, StringComparison.Ordinal));
            }

            _ephemeral.Add(entry);
            if (_ephemeral.Count > EphemeralCapacity)
            {
                _ephemeral.RemoveRange(0, _ephemeral.Count - EphemeralCapacity);
            }
        }
    }

    /// <summary>Users that have something waiting.</summary>
    public IReadOnlyList<Guid> PendingUsers()
    {
        lock (_gate)
        {
            DropStale();
            return [.. _durable.Concat(_ephemeral).Select(e => e.UserId).Distinct()];
        }
    }

    /// <summary>The oldest events of one user, in the order they happened.</summary>
    public IReadOnlyList<QueuedEvent> Peek(Guid userId, int max)
    {
        lock (_gate)
        {
            DropStale();
            return [.. _durable.Concat(_ephemeral)
                .Where(e => e.UserId == userId)
                .OrderBy(e => e.Event.OccurredAt)
                .ThenBy(e => e.Sequence)
                .Take(max)];
        }
    }

    public void Complete(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            _ephemeral.RemoveAll(e => set.Contains(e.Id));
            if (_durable.RemoveAll(e => set.Contains(e.Id)) > 0)
            {
                SaveDurable();
            }
        }
    }

    /// <summary>Forgets everything queued for a user (unlinked, or the token was revoked).</summary>
    public int DropUser(Guid userId)
    {
        lock (_gate)
        {
            var dropped = _ephemeral.RemoveAll(e => e.UserId == userId);
            var durable = _durable.RemoveAll(e => e.UserId == userId);
            if (durable > 0)
            {
                SaveDurable();
            }

            return dropped + durable;
        }
    }

    private void DropStale()
    {
        var cutoff = _time.GetUtcNow() - EphemeralMaxAge;
        _ephemeral.RemoveAll(e => e.EnqueuedAt < cutoff);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<QueuedEvent>>(File.ReadAllText(_path), Json.Options);
            _durable.AddRange(entries ?? []);
            _sequence = _durable.Count == 0 ? 0 : _durable.Max(e => e.Sequence);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var aside = _path + ".corrupt";
            _logger.LogError(ex, "TofuTracker could not read {Path}; moving it to {Aside}. Unsent watches in it are lost.", _path, aside);
            try
            {
                File.Move(_path, aside, overwrite: true);
            }
            catch (IOException)
            {
                // The next save replaces the file.
            }
        }
    }

    private void SaveDurable()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_durable, Json.Options), secret: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The queue still works from memory; only a restart would lose these.
            _logger.LogWarning(ex, "TofuTracker could not save its queue to {Path}.", _path);
        }
    }
}

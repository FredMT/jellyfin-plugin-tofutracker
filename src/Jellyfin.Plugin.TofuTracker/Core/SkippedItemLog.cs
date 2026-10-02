namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>An item that was played but not sent, and why.</summary>
public sealed record SkippedItem(string Name, string Reason, DateTimeOffset At);

/// <summary>
/// A short in-memory list of the items the plugin recently skipped, so an administrator can see on the plugin
/// page why a play never reached TofuTracker. Nothing is persisted; it starts empty with every server run.
/// </summary>
public sealed class SkippedItemLog
{
    public const int Capacity = 20;

    /// <summary>Upper bound of the "already reported" set, so a huge library cannot grow it without limit.</summary>
    private const int MaxRemembered = 5000;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<(string Key, SkippedItem Item)> _recent = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public SkippedItemLog(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Notes that an item was skipped. The list keeps the newest <see cref="Capacity"/> distinct items, newest first;
    /// skipping the same item again only refreshes its time. Returns true the first time this server run sees the
    /// item, which is when the caller should write the log line (so progress ticks and replays stay quiet).
    /// </summary>
    public bool Record(string itemKey, string name, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);

        lock (_gate)
        {
            _recent.RemoveAll(entry => entry.Key == itemKey);
            _recent.Insert(0, (itemKey, new SkippedItem(name, reason, _time.GetUtcNow())));
            if (_recent.Count > Capacity)
            {
                _recent.RemoveRange(Capacity, _recent.Count - Capacity);
            }

            if (_reported.Count >= MaxRemembered)
            {
                _reported.Clear();
            }

            return _reported.Add(itemKey);
        }
    }

    /// <summary>The recently skipped items, newest first.</summary>
    public IReadOnlyList<SkippedItem> Recent()
    {
        lock (_gate)
        {
            return [.. _recent.Select(entry => entry.Item)];
        }
    }
}

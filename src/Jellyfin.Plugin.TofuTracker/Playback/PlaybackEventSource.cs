using System.Threading.Channels;
using Jellyfin.Plugin.TofuTracker.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TofuTracker.Playback;

/// <summary>
/// Listens to Jellyfin's playback and user-data events and feeds the <see cref="PlaybackPlanner"/>.
/// Jellyfin calls the handlers on its request threads (<c>UserDataSaved</c> even inside a database
/// transaction), so they only copy what they need into a channel; a single consumer does the real work.
/// </summary>
public sealed class PlaybackEventSource : IHostedService, IDisposable
{
    private readonly ISessionManager _sessions;
    private readonly IUserDataManager _userData;
    private readonly LinkStore _links;
    private readonly PlaybackPlanner _planner;
    private readonly OutboundQueue _queue;
    private readonly ScrobbleSender _sender;
    private readonly PairingCoordinator _pairing;
    private readonly ILogger _logger;
    private readonly Channel<Work> _channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(5000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _consumer;
    private Task? _senderLoop;

    public PlaybackEventSource(
        ISessionManager sessions,
        IUserDataManager userData,
        LinkStore links,
        PlaybackPlanner planner,
        OutboundQueue queue,
        ScrobbleSender sender,
        PairingCoordinator pairing,
        ILoggerFactory loggerFactory)
    {
        _sessions = sessions;
        _userData = userData;
        _links = links;
        _planner = planner;
        _queue = queue;
        _sender = sender;
        _pairing = pairing;
        _logger = loggerFactory.CreateLogger("Jellyfin.Plugin.TofuTracker");
    }

    private enum PlaybackKind
    {
        Start,
        Progress,
        Stop,
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _consumer = Task.Run(() => ConsumeAsync(_stopping.Token), CancellationToken.None);
        _senderLoop = Task.Run(() => _sender.RunAsync(_stopping.Token), CancellationToken.None);

        _sessions.PlaybackStart += OnPlaybackStart;
        _sessions.PlaybackProgress += OnPlaybackProgress;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _userData.UserDataSaved += OnUserDataSaved;

        _logger.LogInformation("TofuTracker plugin started with {Links} linked user(s) and {Pending} unsent event(s).", _links.All().Count, _queue.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        _sessions.PlaybackProgress -= OnPlaybackProgress;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        _userData.UserDataSaved -= OnUserDataSaved;

        _pairing.CancelAll();
        _channel.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);

        foreach (var task in new[] { _consumer, _senderLoop })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Shutting down; unsent watches are already on disk.
            }
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => Post(PlaybackKind.Start, e, completed: false);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => Post(PlaybackKind.Progress, e, completed: false);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => Post(PlaybackKind.Stop, e, e.PlayedToCompletion);

    private void Post(PlaybackKind kind, PlaybackProgressEventArgs e, bool completed)
    {
        try
        {
            if (e.Item is not (Movie or Episode) || e.Users is null || e.Users.Count == 0)
            {
                return;
            }

            var linked = e.Users.Where(u => _links.GetActive(u.Id) is not null).Select(u => u.Id).ToArray();
            if (linked.Length == 0)
            {
                return;
            }

            _channel.Writer.TryWrite(new PlaybackWork(
                kind,
                linked,
                e.Session?.Id ?? e.DeviceId ?? string.Empty,
                e.Item,
                e.PlaySessionId,
                e.PlaybackPositionTicks,
                e.IsPaused,
                completed));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TofuTracker ignored a playback event it could not read.");
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            // TogglePlayed is the "mark as played / unplayed" action. Playback has its own reasons
            // (PlaybackStart/Progress/Finished), and library imports use Import; both are covered elsewhere.
            if (e.SaveReason != UserDataSaveReason.TogglePlayed
                || e.UserData is not { Played: true }
                || e.Item is not (Movie or Episode)
                || _links.GetActive(e.UserId) is null)
            {
                return;
            }

            _channel.Writer.TryWrite(new ManualWork(e.UserId, e.Item));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TofuTracker ignored a user data event it could not read.");
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Handle(work);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TofuTracker could not process a playback event.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void Handle(Work work)
    {
        switch (work)
        {
            case PlaybackWork playback:
                HandlePlayback(playback);
                break;

            case ManualWork manual:
                HandleManual(manual);
                break;
        }
    }

    private void HandlePlayback(PlaybackWork work)
    {
        var item = new Lazy<ScrobbleItem?>(() => ReadItem(work.Item));
        var runtime = work.Item.RunTimeTicks;

        foreach (var userId in work.UserIds)
        {
            var key = new PlayKey(userId, work.DeviceSessionId, work.Item.Id.ToString("N"));
            var scrobbleEvent = work.Kind switch
            {
                PlaybackKind.Start => _planner.OnStart(key, work.PlaySessionId, work.PositionTicks, runtime, work.IsPaused, () => item.Value),
                PlaybackKind.Progress => _planner.OnProgress(key, work.PlaySessionId, work.PositionTicks, runtime, work.IsPaused, () => item.Value),
                _ => _planner.OnStop(key, work.PlaySessionId, work.PositionTicks, runtime, work.PlayedToCompletion, () => item.Value),
            };

            Enqueue(userId, scrobbleEvent);
        }
    }

    private void HandleManual(ManualWork work)
    {
        Enqueue(work.UserId, _planner.OnManualPlayed(work.UserId, () => ReadItem(work.Item)));
    }

    private void Enqueue(Guid userId, ScrobbleEvent? scrobbleEvent)
    {
        if (scrobbleEvent is null)
        {
            return;
        }

        _logger.LogDebug(
            "TofuTracker queued {Action} for {Kind} {Title} (session {SessionId}).",
            scrobbleEvent.Action,
            scrobbleEvent.Item.Kind,
            scrobbleEvent.Item.Title,
            scrobbleEvent.SessionId);
        _queue.Enqueue(userId, scrobbleEvent);
        _sender.Notify();
    }

    private ScrobbleItem? ReadItem(BaseItem item)
    {
        var facts = JellyfinItemReader.Read(item);
        if (facts is null)
        {
            return null;
        }

        var mapped = ItemMapper.Map(facts);
        if (mapped is null)
        {
            _logger.LogDebug("TofuTracker skipped '{Name}': it has no provider ids (IMDb, TMDb, TVDB, ...). Refresh its metadata.", facts.Name);
        }

        return mapped;
    }

    private abstract record Work;

    private sealed record PlaybackWork(
        PlaybackKind Kind,
        IReadOnlyList<Guid> UserIds,
        string DeviceSessionId,
        BaseItem Item,
        string? PlaySessionId,
        long? PositionTicks,
        bool IsPaused,
        bool PlayedToCompletion) : Work;

    private sealed record ManualWork(Guid UserId, BaseItem Item) : Work;
}

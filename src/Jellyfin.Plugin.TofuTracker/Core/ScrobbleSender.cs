using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>
/// Posts queued events to <c>/v1/events</c>, one batch per linked user (each user has their own token).
/// 5xx, 429 and network errors back off and retry; 401 marks the link broken and stops sending for that
/// user; a 400 is narrowed down to the offending event and drops only that one.
/// </summary>
public sealed class ScrobbleSender
{
    public const int MaxEventsPerBatch = 50;

    /// <summary>The contract allows 256 KiB; stay well under it.</summary>
    public const int MaxBodyBytes = 240_000;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly OutboundQueue _queue;
    private readonly LinkStore _links;
    private readonly Func<HttpClient> _http;
    private readonly Func<string> _serverUrl;
    private readonly Func<ClientInfo> _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Dictionary<Guid, Backoff> _backoff = [];

    public ScrobbleSender(
        OutboundQueue queue,
        LinkStore links,
        Func<HttpClient> http,
        Func<string> serverUrl,
        Func<ClientInfo> client,
        TimeProvider time,
        ILogger logger)
    {
        _queue = queue;
        _links = links;
        _http = http;
        _serverUrl = serverUrl;
        _client = client;
        _time = time;
        _logger = logger;
    }

    /// <summary>Wait this long after a wake-up so a burst (a whole season marked played) goes out as one batch.</summary>
    public TimeSpan CoalesceDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Source of randomness for backoff jitter, in [0, 1).</summary>
    public Func<double> Random { get; init; } = () => System.Random.Shared.NextDouble();

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public string? LastError { get; private set; }

    public int PendingCount => _queue.Count;

    /// <summary>Tells the sender there is something new to send.</summary>
    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan? delay;
            try
            {
                delay = await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TofuTracker sender failed unexpectedly; retrying in 30 seconds.");
                delay = TimeSpan.FromSeconds(30);
            }

            try
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var signalled = _signal.WaitAsync(wake.Token);
                var timer = delay is { } wait
                    ? Task.Delay(wait, _time, wake.Token)
                    : Task.Delay(Timeout.Infinite, wake.Token);
                var first = await Task.WhenAny(signalled, timer).ConfigureAwait(false);
                await wake.CancelAsync().ConfigureAwait(false);
                if (first == signalled && CoalesceDelay > TimeSpan.Zero)
                {
                    await Task.Delay(CoalesceDelay, _time, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass over every user with queued events. Returns when the next pass is due because of a backoff,
    /// or null when nothing is waiting.
    /// </summary>
    public async Task<TimeSpan?> FlushAsync(CancellationToken cancellationToken)
    {
        TimeSpan? next = null;
        foreach (var userId in _queue.PendingUsers())
        {
            var link = _links.GetActive(userId);
            if (link is null)
            {
                var dropped = _queue.DropUser(userId);
                _logger.LogInformation("TofuTracker dropped {Count} queued events of a user that is not linked.", dropped);
                continue;
            }

            var now = _time.GetUtcNow();
            if (_backoff.TryGetValue(userId, out var state) && state.NextAttempt > now)
            {
                next = Min(next, state.NextAttempt - now);
                continue;
            }

            var retryIn = await FlushUserAsync(userId, link, cancellationToken).ConfigureAwait(false);
            next = Min(next, retryIn);
        }

        return next;
    }

    private static TimeSpan? Min(TimeSpan? a, TimeSpan? b)
    {
        if (a is null)
        {
            return b;
        }

        return b is null ? a : a < b ? a : b;
    }

    private async Task<TimeSpan?> FlushUserAsync(Guid userId, UserLink link, CancellationToken cancellationToken)
    {
        // Bounded so one very long queue cannot starve the other users or the wake-up loop.
        for (var round = 0; round < 40; round++)
        {
            var events = _queue.Peek(userId, MaxEventsPerBatch);
            if (events.Count == 0)
            {
                return null;
            }

            var batch = FitToSize(events);
            var result = await PostAsync(link, batch, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case SendOutcome.Accepted:
                    OnAccepted(userId, batch);
                    break;

                case SendOutcome.Unauthorized:
                    OnUnauthorized(userId);
                    return null;

                case SendOutcome.TooLarge when batch.Count > 1:
                    // Cannot happen with FitToSize unless the server's cap is smaller than ours; send smaller.
                    if (await SendOneByOneAsync(userId, link, batch, cancellationToken).ConfigureAwait(false) is { } retryAfterTooLarge)
                    {
                        return retryAfterTooLarge;
                    }

                    break;

                case SendOutcome.Invalid when batch.Count > 1:
                    if (await SendOneByOneAsync(userId, link, batch, cancellationToken).ConfigureAwait(false) is { } retryAfterInvalid)
                    {
                        return retryAfterInvalid;
                    }

                    break;

                case SendOutcome.Invalid:
                case SendOutcome.TooLarge:
                    DropRejected(userId, batch[0], result);
                    break;

                default:
                    return OnFailure(userId, result);
            }
        }

        return TimeSpan.Zero;
    }

    /// <summary>Sends each event alone to find the one the server rejects. Returns a retry delay on a transient failure.</summary>
    private async Task<TimeSpan?> SendOneByOneAsync(Guid userId, UserLink link, IReadOnlyList<QueuedEvent> batch, CancellationToken cancellationToken)
    {
        foreach (var single in batch)
        {
            var result = await PostAsync(link, [single], cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case SendOutcome.Accepted:
                    OnAccepted(userId, [single]);
                    break;

                case SendOutcome.Unauthorized:
                    OnUnauthorized(userId);
                    return TimeSpan.Zero;

                case SendOutcome.Invalid:
                case SendOutcome.TooLarge:
                    DropRejected(userId, single, result);
                    break;

                default:
                    return OnFailure(userId, result);
            }
        }

        return null;
    }

    private static List<QueuedEvent> FitToSize(IReadOnlyList<QueuedEvent> events)
    {
        var count = events.Count;
        while (count > 1 && JsonSerializer.SerializeToUtf8Bytes(events.Take(count).Select(e => e.Event).ToList(), Json.Options).Length > MaxBodyBytes)
        {
            count = (count + 1) / 2;
        }

        return [.. events.Take(count)];
    }

    private void OnAccepted(Guid userId, IReadOnlyList<QueuedEvent> sent)
    {
        _queue.Complete(sent.Select(e => e.Id));
        _backoff.Remove(userId);
        LastSuccessAt = _time.GetUtcNow();
        LastError = null;
    }

    private void OnUnauthorized(Guid userId)
    {
        _links.MarkBroken(userId, _time.GetUtcNow());
        var dropped = _queue.DropUser(userId);
        _backoff.Remove(userId);
        LastError = "TofuTracker rejected the link token; link the user again.";
        _logger.LogWarning(
            "TofuTracker rejected the token of Jellyfin user {UserId} (revoked or unknown). Sending stops for this user until they are linked again; {Count} queued events were dropped.",
            userId,
            dropped);
    }

    private void DropRejected(Guid userId, QueuedEvent rejected, SendResult result)
    {
        _queue.Complete([rejected.Id]);
        _backoff.Remove(userId);
        _logger.LogWarning(
            "TofuTracker rejected a {Action} event (session {SessionId}): {Error}. It was dropped.",
            rejected.Event.Action,
            rejected.Event.SessionId,
            result.Error);
    }

    private TimeSpan OnFailure(Guid userId, SendResult result)
    {
        var failures = _backoff.TryGetValue(userId, out var state) ? state.Failures + 1 : 1;
        TimeSpan delay;
        if (result.RetryAfter is { } retryAfter)
        {
            delay = retryAfter;
        }
        else
        {
            var seconds = Math.Min(MaxBackoff.TotalSeconds, 2 * Math.Pow(2, Math.Min(failures - 1, 10)));
            delay = TimeSpan.FromSeconds(seconds * (0.8 + (0.4 * Random())));
        }

        _backoff[userId] = new Backoff(failures, _time.GetUtcNow() + delay);
        LastError = result.Error;
        if (failures is 1 or 5 or 20)
        {
            _logger.LogWarning("TofuTracker could not send events ({Error}); retrying in {Delay:F0}s (failure {Failures}).", result.Error, delay.TotalSeconds, failures);
        }

        return delay;
    }

    private async Task<SendResult> PostAsync(UserLink link, IReadOnlyList<QueuedEvent> events, CancellationToken cancellationToken)
    {
        var body = new ScrobbleBatch(_client(), [.. events.Select(e => e.Event)]);
        using var request = new HttpRequestMessage(HttpMethod.Post, ScrobblerUrl.Combine(_serverUrl(), "v1/events"))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json.Options)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", link.Token);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TofuTracker-Jellyfin-Plugin", _client().Version));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return Classify(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SendResult(SendOutcome.Retry, null, "request timed out");
        }
        catch (HttpRequestException ex)
        {
            return new SendResult(SendOutcome.Retry, null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private SendResult Classify(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if ((int)status is >= 200 and < 300)
        {
            return new SendResult(SendOutcome.Accepted, null, null);
        }

        var error = $"HTTP {(int)status}";
        return status switch
        {
            HttpStatusCode.Unauthorized => new SendResult(SendOutcome.Unauthorized, null, error),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new SendResult(SendOutcome.Invalid, null, error),
            HttpStatusCode.RequestEntityTooLarge => new SendResult(SendOutcome.TooLarge, null, error),
            HttpStatusCode.TooManyRequests => new SendResult(SendOutcome.Retry, ReadRetryAfter(response), error),
            _ => new SendResult(SendOutcome.Retry, null, error),
        };
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        var wait = header.Delta ?? (header.Date is { } date ? date - _time.GetUtcNow() : null);
        if (wait is null)
        {
            return null;
        }

        return wait.Value < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : wait.Value > MaxRetryAfter ? MaxRetryAfter : wait.Value;
    }

    private enum SendOutcome
    {
        Accepted,
        Unauthorized,
        Invalid,
        TooLarge,
        Retry,
    }

    private sealed record SendResult(SendOutcome Outcome, TimeSpan? RetryAfter, string? Error);

    private sealed record Backoff(int Failures, DateTimeOffset NextAttempt);
}

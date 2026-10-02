using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.TofuTracker.Core;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class ScrobbleSenderTests : IDisposable
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-02T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly FakeHandler _http = new();
    private readonly LinkStore _links;
    private readonly OutboundQueue _queue;
    private readonly ScrobbleSender _sender;

    public ScrobbleSenderTests()
    {
        _links = new LinkStore(_dir.Path, Samples.Logger);
        _links.Upsert(Samples.Link(UserA, "token-a"));
        _links.Upsert(Samples.Link(UserB, "token-b"));
        _queue = new OutboundQueue(_dir.Path, Samples.Logger, _time);
        _sender = NewSender(_queue);
    }

    public void Dispose() => _dir.Dispose();

    private ScrobbleSender NewSender(OutboundQueue queue) => new(
        queue,
        _links,
        _http.CreateClient,
        () => "https://scrobble.test",
        () => Samples.Client,
        _time,
        Samples.Logger)
    {
        CoalesceDelay = TimeSpan.Zero,
        Random = () => 0.5,
    };

    private ScrobbleEvent Event(string action, string session, ScrobbleItem? item = null)
        => Samples.Event(action, session, _time.GetUtcNow(), item);

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    [Fact]
    public async Task Events_are_posted_to_v1_events_with_the_users_token_in_one_batch()
    {
        _queue.Enqueue(UserA, Event(EventActions.Start, "s1", Samples.BreakingBadPilot()));
        _queue.Enqueue(UserA, Event(EventActions.Watched, "s1", Samples.BreakingBadPilot()));

        var next = await _sender.FlushAsync(default);

        Assert.Null(next);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://scrobble.test/v1/events", request.Uri.ToString());
        Assert.Equal("Bearer token-a", request.Authorization);
        Assert.Contains("TofuTracker-Jellyfin-Plugin/1.0.0.0", request.UserAgent, StringComparison.Ordinal);
        Assert.Equal("jellyfin-plugin", request.Json.RootElement.GetProperty("client").GetProperty("name").GetString());
        Assert.Equal(["start", "watched"], request.Events.EnumerateArray().Select(e => e.GetProperty("action").GetString()!).ToArray());
        Assert.Equal(0, _queue.Count);
        Assert.Equal(_time.GetUtcNow(), _sender.LastSuccessAt);
        Assert.Null(_sender.LastError);
    }

    [Fact]
    public async Task Each_user_is_sent_with_their_own_token()
    {
        _queue.Enqueue(UserA, Event(EventActions.Watched, "a"));
        _queue.Enqueue(UserB, Event(EventActions.Watched, "b"));

        await _sender.FlushAsync(default);

        Assert.Equal(["Bearer token-a", "Bearer token-b"], _http.Requests.Select(r => r.Authorization!).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(_http.Requests, r => Assert.Single(r.Events.EnumerateArray()));
    }

    [Fact]
    public async Task Server_errors_back_off_exponentially_and_retry_without_losing_the_watch()
    {
        _http.Respond = _ => Status(HttpStatusCode.ServiceUnavailable);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        Assert.Equal(TimeSpan.FromSeconds(2), await _sender.FlushAsync(default));
        Assert.Single(_http.Requests);

        // Too early: nothing is sent, and the sender says when to come back.
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), await _sender.FlushAsync(default));
        Assert.Single(_http.Requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(4), await _sender.FlushAsync(default));
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(8), await _sender.FlushAsync(default));
        Assert.Equal(3, _http.Requests.Count);
        Assert.Equal(1, _queue.Count);
        Assert.Equal("HTTP 503", _sender.LastError);

        _http.Respond = _ => Status(HttpStatusCode.Accepted);
        _time.Advance(TimeSpan.FromSeconds(8));
        Assert.Null(await _sender.FlushAsync(default));
        Assert.Equal(0, _queue.Count);
        Assert.Null(_sender.LastError);
    }

    [Fact]
    public async Task Backoff_is_capped_at_five_minutes()
    {
        _http.Respond = _ => Status(HttpStatusCode.BadGateway);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        TimeSpan? last = null;
        for (var i = 0; i < 15; i++)
        {
            last = await _sender.FlushAsync(default);
            _time.Advance(last!.Value);
        }

        Assert.Equal(TimeSpan.FromMinutes(5), last);
    }

    [Fact]
    public async Task Jitter_keeps_the_delay_within_twenty_percent()
    {
        var low = NewSenderWithRandom(0.0);
        var high = NewSenderWithRandom(0.999);
        _http.Respond = _ => Status(HttpStatusCode.InternalServerError);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        var lowDelay = await low.FlushAsync(default);
        // A fresh sender (its own backoff state) over the same queue.
        var highDelay = await high.FlushAsync(default);

        Assert.InRange(lowDelay!.Value.TotalSeconds, 1.59, 1.61);
        Assert.InRange(highDelay!.Value.TotalSeconds, 2.39, 2.41);
    }

    private ScrobbleSender NewSenderWithRandom(double random) => new(
        _queue,
        _links,
        _http.CreateClient,
        () => "https://scrobble.test",
        () => Samples.Client,
        _time,
        Samples.Logger)
    {
        CoalesceDelay = TimeSpan.Zero,
        Random = () => random,
    };

    [Fact]
    public async Task Too_many_requests_waits_for_retry_after()
    {
        var response = Status(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        _http.Respond = _ => response;
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        Assert.Equal(TimeSpan.FromSeconds(120), await _sender.FlushAsync(default));
        _time.Advance(TimeSpan.FromSeconds(119));
        await _sender.FlushAsync(default);
        Assert.Single(_http.Requests);

        _http.Respond = _ => Status(HttpStatusCode.Accepted);
        _time.Advance(TimeSpan.FromSeconds(1));
        await _sender.FlushAsync(default);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Retry_after_is_clamped()
    {
        var response = Status(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(3));
        _http.Respond = _ => response;
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        Assert.Equal(TimeSpan.FromHours(1), await _sender.FlushAsync(default));
    }

    [Fact]
    public async Task Network_errors_are_retried()
    {
        _http.Respond = _ => throw new HttpRequestException("connection refused");
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        Assert.Equal(TimeSpan.FromSeconds(2), await _sender.FlushAsync(default));
        Assert.Equal(1, _queue.Count);
        Assert.Equal("connection refused", _sender.LastError);
    }

    [Fact]
    public async Task Unauthorized_marks_the_link_broken_drops_the_queue_and_stops_sending()
    {
        _http.Respond = _ => Status(HttpStatusCode.Unauthorized);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w1"));
        _queue.Enqueue(UserA, Event(EventActions.Pause, "s1"));
        _queue.Enqueue(UserB, Event(EventActions.Watched, "w2"));

        await _sender.FlushAsync(default);

        Assert.True(_links.Get(UserA)?.Broken);
        Assert.Null(_links.GetActive(UserA));
        // The fake rejects every token, so user B is broken too.
        Assert.Null(_links.GetActive(UserB));
        Assert.Equal(0, _queue.Count);
        Assert.Contains("link", _sender.LastError, StringComparison.OrdinalIgnoreCase);

        _http.Requests.Clear();
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w3"));
        await _sender.FlushAsync(default);
        Assert.Empty(_http.Requests);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Only_the_user_whose_token_was_rejected_is_marked_broken()
    {
        _http.Respond = r => r.Authorization == "Bearer token-a" ? Status(HttpStatusCode.Unauthorized) : Status(HttpStatusCode.Accepted);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w1"));
        _queue.Enqueue(UserB, Event(EventActions.Watched, "w2"));

        await _sender.FlushAsync(default);

        Assert.True(_links.Get(UserA)?.Broken);
        Assert.NotNull(_links.GetActive(UserB));
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task A_rejected_event_in_a_batch_is_found_and_dropped_while_the_rest_is_delivered()
    {
        _http.Respond = r => r.Events.GetArrayLength() > 1 || r.Body.Contains("\"sessionId\":\"bad\"", StringComparison.Ordinal)
            ? Status(HttpStatusCode.BadRequest)
            : Status(HttpStatusCode.Accepted);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "good1"));
        _time.Advance(TimeSpan.FromSeconds(1));
        _queue.Enqueue(UserA, Event(EventActions.Watched, "bad"));
        _time.Advance(TimeSpan.FromSeconds(1));
        _queue.Enqueue(UserA, Event(EventActions.Watched, "good2"));

        var next = await _sender.FlushAsync(default);

        Assert.Null(next);
        Assert.Equal(0, _queue.Count);
        var delivered = _http.Requests.Where(r => r.Events.GetArrayLength() == 1 && !r.Body.Contains("\"bad\"", StringComparison.Ordinal)).Count();
        Assert.Equal(2, delivered);
        Assert.Equal(4, _http.Requests.Count);
        Assert.NotNull(_links.GetActive(UserA));
    }

    [Fact]
    public async Task A_single_rejected_event_is_dropped_not_retried()
    {
        _http.Respond = _ => Status(HttpStatusCode.UnprocessableEntity);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        Assert.Null(await _sender.FlushAsync(default));
        Assert.Single(_http.Requests);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Batches_hold_at_most_fifty_events()
    {
        for (var i = 0; i < 120; i++)
        {
            _queue.Enqueue(UserA, Event(EventActions.Watched, "w" + i));
        }

        await _sender.FlushAsync(default);

        Assert.Equal([50, 50, 20], _http.Requests.Select(r => r.Events.GetArrayLength()).ToArray());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Batches_stay_under_the_body_size_limit()
    {
        var bigTitle = new string('t', 6000);
        var item = new ScrobbleItem(ItemKinds.Movie, bigTitle, new TitleIds(Tmdb: 1));
        for (var i = 0; i < 50; i++)
        {
            // Long session ids and titles inflate each event.
            _queue.Enqueue(UserA, Samples.Event(EventActions.Watched, new string('s', 190) + i, _time.GetUtcNow(), item));
        }

        // 50 events of ~6 KB would be 300 KB: the sender has to split them.
        await _sender.FlushAsync(default);

        Assert.True(_http.Requests.Count >= 2);
        Assert.All(_http.Requests, r => Assert.True(r.Body.Length <= ScrobbleSender.MaxBodyBytes));
        Assert.Equal(0, _queue.Count);
        Assert.Equal(50, _http.Requests.Sum(r => r.Events.GetArrayLength()));
    }

    [Fact]
    public async Task Watched_events_queued_during_an_outage_are_delivered_after_a_restart()
    {
        _http.Respond = _ => Status(HttpStatusCode.ServiceUnavailable);
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w-before-restart"));
        await _sender.FlushAsync(default);
        Assert.Equal(1, _queue.Count);

        // Jellyfin restarts: new queue and sender over the same folder.
        var restartedQueue = new OutboundQueue(_dir.Path, Samples.Logger, _time);
        var restarted = NewSender(restartedQueue);
        _http.Respond = _ => Status(HttpStatusCode.Accepted);
        _http.Requests.Clear();

        Assert.Null(await restarted.FlushAsync(default));

        var request = Assert.Single(_http.Requests);
        Assert.Equal("w-before-restart", request.Events[0].GetProperty("sessionId").GetString());
        Assert.Equal(0, restartedQueue.Count);
    }

    [Fact]
    public async Task Stale_live_events_are_not_sent_late()
    {
        _http.Respond = _ => Status(HttpStatusCode.ServiceUnavailable);
        _queue.Enqueue(UserA, Event(EventActions.Progress, "s1"));
        await _sender.FlushAsync(default);
        Assert.Equal(1, _queue.Count);

        _http.Respond = _ => Status(HttpStatusCode.Accepted);
        _http.Requests.Clear();
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Null(await _sender.FlushAsync(default));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Events_of_a_user_who_is_no_longer_linked_are_dropped()
    {
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));
        _links.Remove(UserA);

        await _sender.FlushAsync(default);

        Assert.Empty(_http.Requests);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task The_token_is_never_part_of_the_request_body()
    {
        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));

        await _sender.FlushAsync(default);

        Assert.DoesNotContain("token-a", _http.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_loop_sends_when_notified_and_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => _sender.RunAsync(cts.Token));

        _queue.Enqueue(UserA, Event(EventActions.Watched, "w"));
        _sender.Notify();

        await WaitUntilAsync(() => _http.Requests.Count == 1);
        Assert.Equal(0, _queue.Count);

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }
}

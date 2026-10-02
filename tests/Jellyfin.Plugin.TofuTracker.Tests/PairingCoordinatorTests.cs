using System.Net;
using Jellyfin.Plugin.TofuTracker.Core;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class PairingCoordinatorTests : IDisposable
{
    private static readonly Guid User = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private const string StartJson = """
        {"deviceCode":"device-code-0123456789-0123456789-0123456789","userCode":"ABCD-EFGH","verificationUrl":"https://tofutracker.com/link?code=ABCD-EFGH","interval":3,"expiresIn":600}
        """;

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-02T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly FakeHandler _http = new();
    private readonly LinkStore _links;
    private readonly PairingCoordinator _pairing;

    public PairingCoordinatorTests()
    {
        _links = new LinkStore(_dir.Path, Samples.Logger);
        _pairing = new PairingCoordinator(_http.CreateClient, () => "https://scrobble.test", () => Samples.Client, _links, _time, Samples.Logger);
        _http.Respond = r => r.Uri.AbsolutePath switch
        {
            "/v1/pair/start" => FakeHandler.Json(HttpStatusCode.OK, StartJson),
            "/v1/pair/poll" => FakeHandler.Json(HttpStatusCode.OK, """{"status":"pending"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    public void Dispose()
    {
        _pairing.CancelAll();
        _dir.Dispose();
    }

    [Fact]
    public async Task Start_posts_the_adapter_and_label_and_shows_only_the_code_and_link()
    {
        var view = await _pairing.StartAsync(User, "Living room / alice", default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://scrobble.test/v1/pair/start", request.Uri.ToString());
        Assert.Null(request.Authorization);
        Assert.Equal("jellyfin", request.Json.RootElement.GetProperty("adapter").GetString());
        Assert.Equal("Living room / alice", request.Json.RootElement.GetProperty("label").GetString());

        Assert.Equal(PairingState.Pending, view.State);
        Assert.Equal("ABCD-EFGH", view.UserCode);
        Assert.Equal("https://tofutracker.com/link?code=ABCD-EFGH", view.VerificationUrl);
        Assert.Equal(_time.GetUtcNow().AddSeconds(600), view.ExpiresAt);
        Assert.DoesNotContain("device-code", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polling_while_pending_changes_nothing_and_sends_the_device_code()
    {
        await _pairing.StartAsync(User, "x", default);

        var view = await _pairing.PollOnceAsync(User, default);

        Assert.Equal(PairingState.Pending, view.State);
        var poll = _http.Requests.Last(r => r.Uri.AbsolutePath == "/v1/pair/poll");
        Assert.Equal("device-code-0123456789-0123456789-0123456789", poll.Json.RootElement.GetProperty("deviceCode").GetString());
        Assert.Null(_links.Get(User));
    }

    [Fact]
    public async Task Approval_stores_the_token_and_never_exposes_it()
    {
        await _pairing.StartAsync(User, "x", default);
        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"approved","token":"secret-token-xyz","connectionId":"conn-1","username":"kalugu"}""");

        var view = await _pairing.PollOnceAsync(User, default);

        Assert.Equal(PairingState.Approved, view.State);
        Assert.Equal("kalugu", view.Username);
        var link = _links.GetActive(User);
        Assert.Equal("secret-token-xyz", link?.Token);
        Assert.Equal("conn-1", link?.ConnectionId);
        Assert.Equal("kalugu", link?.Username);
        Assert.DoesNotContain("secret-token-xyz", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-xyz", System.Text.Json.JsonSerializer.Serialize(_pairing.Status(User)), StringComparison.Ordinal);

        // Further polls do not call the server again.
        var calls = _http.Requests.Count;
        await _pairing.PollOnceAsync(User, default);
        Assert.Equal(calls, _http.Requests.Count);
    }

    [Fact]
    public async Task Approval_without_a_token_fails_instead_of_linking()
    {
        await _pairing.StartAsync(User, "x", default);
        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"approved"}""");

        var view = await _pairing.PollOnceAsync(User, default);

        Assert.Equal(PairingState.Failed, view.State);
        Assert.Null(_links.Get(User));
    }

    [Theory]
    [InlineData("denied", PairingState.Denied)]
    [InlineData("expired", PairingState.Expired)]
    public async Task Denied_and_expired_end_the_pairing_without_a_link(string status, PairingState expected)
    {
        await _pairing.StartAsync(User, "x", default);
        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, $$"""{"status":"{{status}}"}""");

        var view = await _pairing.PollOnceAsync(User, default);

        Assert.Equal(expected, view.State);
        Assert.NotNull(view.Message);
        Assert.Null(_links.Get(User));
    }

    [Fact]
    public async Task The_pairing_expires_locally_when_the_time_is_up()
    {
        await _pairing.StartAsync(User, "x", default);
        _time.Advance(TimeSpan.FromSeconds(601));
        var calls = _http.Requests.Count;

        var view = await _pairing.PollOnceAsync(User, default);

        Assert.Equal(PairingState.Expired, view.State);
        Assert.Equal(calls, _http.Requests.Count);
    }

    [Fact]
    public async Task A_network_error_keeps_the_pairing_alive()
    {
        await _pairing.StartAsync(User, "x", default);
        _http.Respond = _ => throw new HttpRequestException("offline");

        var view = await _pairing.PollOnceAsync(User, default);

        Assert.Equal(PairingState.Pending, view.State);
        Assert.Contains("offline", view.Message, StringComparison.Ordinal);

        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"approved","token":"t","connectionId":"c","username":"u"}""");
        Assert.Equal(PairingState.Approved, (await _pairing.PollOnceAsync(User, default)).State);
    }

    [Fact]
    public async Task Start_fails_with_a_readable_error_when_the_scrobbler_is_unreachable_or_unhappy()
    {
        _http.Respond = _ => throw new HttpRequestException("no route");
        var offline = await Assert.ThrowsAsync<PairingException>(() => _pairing.StartAsync(User, "x", default));
        Assert.Contains("no route", offline.Message, StringComparison.Ordinal);

        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var limited = await Assert.ThrowsAsync<PairingException>(() => _pairing.StartAsync(User, "x", default));
        Assert.Contains("slow down", limited.Message, StringComparison.Ordinal);

        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<PairingException>(() => _pairing.StartAsync(User, "x", default));

        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, "<html>");
        await Assert.ThrowsAsync<PairingException>(() => _pairing.StartAsync(User, "x", default));

        Assert.Equal(PairingState.None, _pairing.Status(User).State);
    }

    [Fact]
    public async Task Starting_again_replaces_the_previous_code_and_cancel_forgets_it()
    {
        await _pairing.StartAsync(User, "x", default);
        _http.Respond = r => r.Uri.AbsolutePath == "/v1/pair/start"
            ? FakeHandler.Json(HttpStatusCode.OK, StartJson.Replace("ABCD-EFGH", "WXYZ-2345", StringComparison.Ordinal))
            : FakeHandler.Json(HttpStatusCode.OK, """{"status":"pending"}""");

        var second = await _pairing.StartAsync(User, "x", default);

        Assert.Equal("WXYZ-2345", second.UserCode);
        Assert.Equal("WXYZ-2345", _pairing.Status(User).UserCode);

        _pairing.Cancel(User);
        Assert.Equal(PairingState.None, _pairing.Status(User).State);
    }

    [Fact]
    public async Task Label_is_limited_to_100_characters()
    {
        await _pairing.StartAsync(User, new string('L', 300), default);

        Assert.Equal(100, _http.Requests[0].Json.RootElement.GetProperty("label").GetString()!.Length);
    }

    [Fact]
    public async Task The_background_poll_links_the_user_without_the_page_being_open()
    {
        // Use the real clock for this one: the loop sleeps for the server's interval (clamped to 2 s minimum).
        using var dir = new TempDir();
        var links = new LinkStore(dir.Path, Samples.Logger);
        var http = new FakeHandler
        {
            Respond = r => r.Uri.AbsolutePath == "/v1/pair/start"
                ? FakeHandler.Json(HttpStatusCode.OK, StartJson.Replace("\"interval\":3", "\"interval\":0", StringComparison.Ordinal))
                : FakeHandler.Json(HttpStatusCode.OK, """{"status":"approved","token":"bg-token","connectionId":"c","username":"u"}"""),
        };
        var pairing = new PairingCoordinator(http.CreateClient, () => "https://scrobble.test", () => Samples.Client, links, TimeProvider.System, Samples.Logger);

        await pairing.StartAsync(User, "x", default);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (links.GetActive(User) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal("bg-token", links.GetActive(User)?.Token);
        pairing.CancelAll();
    }
}

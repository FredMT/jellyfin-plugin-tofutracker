using System.Net;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.TofuTracker.Api;
using Jellyfin.Plugin.TofuTracker.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Jellyfin.Plugin.TofuTracker.Tests;

[Collection(JellyfinStaticsCollection.Name)]
public sealed class TofuTrackerControllerTests : IDisposable
{
    private const string StartJson = """{"deviceCode":"dev-code","userCode":"ABCD-EFGH","verificationUrl":"https://tofutracker.com/link?code=ABCD-EFGH","interval":3,"expiresIn":600}""";

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-02T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly FakeHandler _http = new();
    private readonly Jellyfin.Database.Implementations.Entities.User _alice = JellyfinFixtures.NewUser("alice");
    private readonly Jellyfin.Database.Implementations.Entities.User _bob = JellyfinFixtures.NewUser("Bob");
    private readonly LinkStore _links;
    private readonly SkippedItemLog _skipped;
    private readonly PairingCoordinator _pairing;
    private readonly TofuTrackerController _controller;

    public TofuTrackerControllerTests()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginsPath.Returns(Path.Combine(_dir.Path, "plugins"));
        paths.PluginConfigurationsPath.Returns(Path.Combine(_dir.Path, "configurations"));
        Directory.CreateDirectory(paths.PluginConfigurationsPath);
        _ = new Plugin(paths, new TestXmlSerializer());

        _links = new LinkStore(Path.Combine(_dir.Path, "data"), Samples.Logger);
        _pairing = new PairingCoordinator(_http.CreateClient, ScrobblerUrlOrDefault, () => Samples.Client, _links, _time, Samples.Logger);
        var queue = new OutboundQueue(Path.Combine(_dir.Path, "data"), Samples.Logger, _time);
        var sender = new ScrobbleSender(queue, _links, _http.CreateClient, ScrobblerUrlOrDefault, () => Samples.Client, _time, Samples.Logger);

        var users = Substitute.For<IUserManager>();
        users.GetUsers().Returns([_alice, _bob]);
        users.GetUserById(_alice.Id).Returns(_alice);
        users.GetUserById(_bob.Id).Returns(_bob);
        var host = Substitute.For<IServerApplicationHost>();
        host.FriendlyName.Returns("Living room");

        _skipped = new SkippedItemLog(_time);
        _controller = new TofuTrackerController(users, host, _links, _pairing, sender, _skipped);
        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, StartJson);
    }

    public void Dispose()
    {
        _pairing.CancelAll();
        _dir.Dispose();
    }

    private static string ScrobblerUrlOrDefault() => ScrobblerUrl.NormalizeOrDefault(Plugin.Instance?.Configuration.ServerUrl);

    private static string Serialize(object? value) => JsonSerializer.Serialize(value);

    [Fact]
    public void Every_endpoint_requires_an_administrator()
    {
        var type = typeof(TofuTrackerController);
        var authorize = type.GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("RequiresElevation", authorize?.Policy);
        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(actions);
        Assert.All(actions, m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
        Assert.All(actions, m => Assert.NotNull(m.GetCustomAttribute<HttpMethodAttribute>()));
    }

    [Fact]
    public void Status_lists_users_by_name_and_never_contains_a_token()
    {
        _links.Upsert(Samples.Link(_alice.Id, "super-secret-token", "alice-tt"));

        var status = _controller.GetStatus().Value!;
        var json = Serialize(status);

        Assert.Equal(["alice", "Bob"], status.Users.Select(u => u.Name).ToArray());
        var alice = status.Users[0];
        Assert.True(alice.Linked);
        Assert.False(alice.LinkBroken);
        Assert.Equal("alice-tt", alice.TofuTrackerUsername);
        Assert.False(status.Users[1].Linked);
        Assert.DoesNotContain("super-secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScrobblerUrl.Default, status.ServerUrl);
    }

    [Fact]
    public void Status_lists_the_recently_skipped_items_newest_first()
    {
        Assert.Empty(_controller.GetStatus().Value!.NotSent);

        _skipped.Record("a", "Home video", "no provider ids");
        _time.Advance(TimeSpan.FromMinutes(1));
        _skipped.Record("b", "Show - Pilot", "no provider ids");

        var notSent = _controller.GetStatus().Value!.NotSent;

        Assert.Equal(["Show - Pilot", "Home video"], notSent.Select(i => i.Name).ToArray());
        Assert.Equal("no provider ids", notSent[0].Reason);
        Assert.Equal(_time.GetUtcNow(), notSent[0].At);
        Assert.Contains("\"notSent\"", Serialize(_controller.GetStatus().Value), StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_link_is_reported_as_such()
    {
        _links.Upsert(Samples.Link(_alice.Id));
        _links.MarkBroken(_alice.Id, _time.GetUtcNow());

        var alice = _controller.GetStatus().Value!.Users[0];

        Assert.True(alice.Linked);
        Assert.True(alice.LinkBroken);
    }

    [Fact]
    public async Task Starting_a_link_returns_the_code_and_url_but_not_the_device_code()
    {
        var result = await _controller.StartLink(new TofuTrackerController.UserRequest { UserId = _bob.Id }, default);

        var user = result.Value!;
        Assert.Equal("pending", user.Pairing?.State);
        Assert.Equal("ABCD-EFGH", user.Pairing?.UserCode);
        Assert.Equal("https://tofutracker.com/link?code=ABCD-EFGH", user.Pairing?.VerificationUrl);
        Assert.DoesNotContain("dev-code", Serialize(user), StringComparison.Ordinal);
        Assert.Equal("Living room / Bob", _http.Requests[0].Json.RootElement.GetProperty("label").GetString());
        Assert.Equal("https://scrobble.tofutracker.com/v1/pair/start", _http.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Linking_an_unknown_user_is_a_404_and_a_scrobbler_failure_is_a_502_with_a_message()
    {
        var unknown = await _controller.StartLink(new TofuTrackerController.UserRequest { UserId = Guid.NewGuid() }, default);
        Assert.IsType<NotFoundObjectResult>(unknown.Result);

        _http.Respond = _ => throw new HttpRequestException("offline");
        var failed = await _controller.StartLink(new TofuTrackerController.UserRequest { UserId = _alice.Id }, default);
        var object502 = Assert.IsType<ObjectResult>(failed.Result);
        Assert.Equal(502, object502.StatusCode);
        Assert.Contains("offline", Serialize(object502.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_approval_the_page_sees_the_linked_state_and_still_no_token()
    {
        await _controller.StartLink(new TofuTrackerController.UserRequest { UserId = _alice.Id }, default);
        _http.Respond = _ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"approved","token":"fresh-secret","connectionId":"c1","username":"alice-tt"}""");
        await _pairing.PollOnceAsync(_alice.Id, default);

        var user = _controller.GetLinkStatus(_alice.Id).Value!;

        Assert.True(user.Linked);
        Assert.Equal("alice-tt", user.TofuTrackerUsername);
        Assert.Equal("approved", user.Pairing?.State);
        Assert.DoesNotContain("fresh-secret", Serialize(user), StringComparison.Ordinal);
        Assert.DoesNotContain("fresh-secret", Serialize(_controller.GetStatus().Value), StringComparison.Ordinal);
    }

    [Fact]
    public void Link_status_of_an_unknown_user_is_a_404()
    {
        Assert.IsType<NotFoundObjectResult>(_controller.GetLinkStatus(Guid.NewGuid()).Result);
    }

    [Fact]
    public async Task Unlink_forgets_the_token_and_cancels_a_pending_pairing()
    {
        _links.Upsert(Samples.Link(_alice.Id));
        await _controller.StartLink(new TofuTrackerController.UserRequest { UserId = _alice.Id }, default);

        Assert.IsType<NoContentResult>(_controller.Unlink(new TofuTrackerController.UserRequest { UserId = _alice.Id }));

        var user = _controller.GetLinkStatus(_alice.Id).Value!;
        Assert.False(user.Linked);
        Assert.Null(user.Pairing);
        Assert.Null(new LinkStore(Path.Combine(_dir.Path, "data"), Samples.Logger).Get(_alice.Id));
    }

    [Fact]
    public async Task Cancel_stops_a_pending_pairing()
    {
        await _controller.StartLink(new TofuTrackerController.UserRequest { UserId = _alice.Id }, default);

        Assert.IsType<NoContentResult>(_controller.CancelLink(new TofuTrackerController.UserRequest { UserId = _alice.Id }));

        Assert.Null(_controller.GetLinkStatus(_alice.Id).Value!.Pairing);
    }

    [Fact]
    public void Saving_settings_validates_normalises_and_persists_the_server_url()
    {
        var bad = _controller.SaveSettings(new TofuTrackerController.SettingsDto { ServerUrl = "http://evil.example" });
        Assert.IsType<BadRequestObjectResult>(bad.Result);
        Assert.Equal(ScrobblerUrl.Default, Plugin.Instance!.Configuration.ServerUrl);

        var ok = _controller.SaveSettings(new TofuTrackerController.SettingsDto { ServerUrl = " https://scrobble.example.org/ " });

        Assert.Equal("https://scrobble.example.org", ok.Value!.ServerUrl);
        Assert.Equal("https://scrobble.example.org", Plugin.Instance.Configuration.ServerUrl);
        Assert.Equal("https://scrobble.example.org", _controller.GetStatus().Value!.ServerUrl);
        Assert.True(File.Exists(Plugin.Instance.ConfigurationFilePath));
        Assert.DoesNotContain("token", File.ReadAllText(Plugin.Instance.ConfigurationFilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_configured_server_url_is_used_for_pairing()
    {
        _controller.SaveSettings(new TofuTrackerController.SettingsDto { ServerUrl = "https://scrobble.example.org" });

        await _controller.StartLink(new TofuTrackerController.UserRequest { UserId = _alice.Id }, default);

        Assert.Equal("https://scrobble.example.org/v1/pair/start", _http.Requests[0].Uri.ToString());
    }
}

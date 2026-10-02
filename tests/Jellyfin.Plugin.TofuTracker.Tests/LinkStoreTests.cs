using Jellyfin.Plugin.TofuTracker.Core;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class LinkStoreTests : IDisposable
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private LinkStore Store() => new(_dir.Path, Samples.Logger);

    [Fact]
    public void Links_survive_a_restart()
    {
        Store().Upsert(Samples.Link(UserA, "token-a", "alice"));
        Store().Upsert(Samples.Link(UserB, "token-b", "bob"));

        var reloaded = Store();

        Assert.Equal("token-a", reloaded.Get(UserA)?.Token);
        Assert.Equal("bob", reloaded.Get(UserB)?.Username);
        Assert.Equal(2, reloaded.All().Count);
    }

    [Fact]
    public void Relinking_replaces_the_old_token_and_clears_a_broken_flag()
    {
        var store = Store();
        store.Upsert(Samples.Link(UserA, "old"));
        store.MarkBroken(UserA, DateTimeOffset.UtcNow);
        Assert.Null(store.GetActive(UserA));

        store.Upsert(Samples.Link(UserA, "new"));

        Assert.Equal("new", store.GetActive(UserA)?.Token);
    }

    [Fact]
    public void A_broken_link_is_remembered_across_restarts_but_is_not_active()
    {
        var store = Store();
        store.Upsert(Samples.Link(UserA));
        store.MarkBroken(UserA, DateTimeOffset.Parse("2026-10-02T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        var reloaded = Store();

        Assert.True(reloaded.Get(UserA)?.Broken);
        Assert.NotNull(reloaded.Get(UserA)?.BrokenAt);
        Assert.Null(reloaded.GetActive(UserA));
    }

    [Fact]
    public void Remove_forgets_the_link()
    {
        var store = Store();
        store.Upsert(Samples.Link(UserA));

        Assert.True(store.Remove(UserA));
        Assert.False(store.Remove(UserA));
        Assert.Null(Store().Get(UserA));
    }

    [Fact]
    public void The_token_file_is_readable_only_by_its_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Store().Upsert(Samples.Link(UserA));

        var mode = File.GetUnixFileMode(Path.Combine(_dir.Path, "links.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void A_corrupt_file_is_set_aside_instead_of_crashing_the_plugin()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "links.json"), "garbage");

        var store = Store();

        Assert.Empty(store.All());
        Assert.True(File.Exists(Path.Combine(_dir.Path, "links.json.corrupt")));
    }

    [Fact]
    public void No_temp_file_is_left_behind()
    {
        Store().Upsert(Samples.Link(UserA));

        Assert.False(File.Exists(Path.Combine(_dir.Path, "links.json.tmp")));
    }
}

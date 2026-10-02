using System.Text.Json;
using Jellyfin.Plugin.TofuTracker.Core;

namespace Jellyfin.Plugin.TofuTracker.Tests;

public class ItemMapperTests
{
    private static Dictionary<string, string> Ids(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static MediaFacts Movie(params (string, string)[] ids)
        => new(MediaKind.Movie, "The Matrix", null, null, null, Ids(ids), null);

    private static MediaFacts Episode(Dictionary<string, string>? series, params (string, string)[] episodeIds)
        => new(MediaKind.Episode, "Pilot", "Breaking Bad", 1, 1, Ids(episodeIds), series);

    [Fact]
    public void Movie_uses_its_own_ids()
    {
        var item = ItemMapper.Map(Movie(("Imdb", "tt0133093"), ("Tmdb", "603")));

        Assert.NotNull(item);
        Assert.Equal(ItemKinds.Movie, item.Kind);
        Assert.Equal("The Matrix", item.Title);
        Assert.Equal("tt0133093", item.Ids.Imdb);
        Assert.Equal(603, item.Ids.Tmdb);
        Assert.Null(item.EpisodeIds);
        Assert.Null(item.Season);
        Assert.Null(item.Numbering);
    }

    [Fact]
    public void Episode_sends_series_ids_as_title_ids_and_its_own_ids_as_episode_ids()
    {
        var series = Ids(("Imdb", "tt0903747"), ("Tmdb", "1396"), ("Tvdb", "81189"));
        var item = ItemMapper.Map(Episode(series, ("Tvdb", "349232")));

        Assert.NotNull(item);
        Assert.Equal(ItemKinds.Episode, item.Kind);
        Assert.Equal("Breaking Bad - Pilot", item.Title);
        Assert.Equal(new TitleIds(Imdb: "tt0903747", Tmdb: 1396, Tvdb: 81189), item.Ids);
        Assert.Equal(new EpisodeIds(Tvdb: 349232), item.EpisodeIds);
        Assert.Equal(1, item.Season);
        Assert.Equal(1, item.Episode);
        Assert.Equal(Numbering.Tvdb, item.Numbering);
    }

    [Fact]
    public void Episode_ids_never_leak_into_title_ids()
    {
        // A TMDb-only library: the episode's Tmdb id is an episode id, not the show.
        var item = ItemMapper.Map(Episode(Ids(("Tmdb", "1396")), ("Tmdb", "62085")));

        Assert.NotNull(item);
        Assert.Equal(1396, item.Ids.Tmdb);
        Assert.Equal(62085, item.EpisodeIds!.Tmdb);
    }

    [Fact]
    public void Anime_ids_come_from_the_series()
    {
        var series = Ids(("Tvdb", "219121"), ("AniDB", "23"), ("AniList", "1"), ("MyAnimeList", "1"), ("Kitsu", "1"));
        var item = ItemMapper.Map(Episode(series, ("Tvdb", "4001")));

        Assert.NotNull(item);
        Assert.Equal(219121, item.Ids.Tvdb);
        Assert.Equal(23, item.Ids.Anidb);
        Assert.Equal(1, item.Ids.Anilist);
        Assert.Equal(1, item.Ids.Mal);
        Assert.Equal(1, item.Ids.Kitsu);
    }

    [Fact]
    public void Provider_keys_are_matched_without_regard_to_case()
    {
        var series = new Dictionary<string, string> { ["anidb"] = "69", ["MAL"] = "21", ["tvdb"] = "81797" };
        var item = ItemMapper.Map(Episode(series));

        Assert.NotNull(item);
        Assert.Equal(69, item.Ids.Anidb);
        Assert.Equal(21, item.Ids.Mal);
        Assert.Equal(81797, item.Ids.Tvdb);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12 34")]
    [InlineData("")]
    public void Bad_numeric_ids_are_dropped(string value)
    {
        Assert.Null(ItemMapper.ParseNumericId(value));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("nm0000093")]
    [InlineData("tt")]
    [InlineData("TT0133093")]
    public void Bad_imdb_ids_are_dropped(string value)
    {
        Assert.Null(ItemMapper.ParseImdbId(value));
    }

    [Fact]
    public void An_item_without_any_provider_id_is_not_sent()
    {
        Assert.Null(ItemMapper.Map(Movie()));
        Assert.Null(ItemMapper.Map(Movie(("Tmdb", "nope"))));
        Assert.Null(ItemMapper.Map(Episode(Ids(("Tvdb", "x")))));
        Assert.Null(ItemMapper.Map(Episode(null)));
    }

    [Fact]
    public void An_episode_known_only_by_its_own_id_is_still_sent()
    {
        var item = ItemMapper.Map(Episode(null, ("Tvdb", "349232")));

        Assert.NotNull(item);
        Assert.True(item.Ids.IsEmpty);
        Assert.Equal(349232, item.EpisodeIds!.Tvdb);
    }

    [Theory]
    [InlineData("Tvdb", "Tvdb", "tvdb")]
    [InlineData("Tvdb", "Tmdb", "tvdb")]
    [InlineData("Tmdb", "Tvdb", "tmdb")]
    [InlineData("Tmdb", "", "tmdb")]
    [InlineData("", "Tvdb", "tvdb")]
    [InlineData("", "Tmdb", "tmdb")]
    // The episode's own id says which provider numbered it, even when the series also carries a TVDB id.
    public void Numbering_follows_the_provider_that_matched_the_episode(string episodeProvider, string seriesProvider, string expected)
    {
        var episode = episodeProvider.Length == 0 ? [] : new[] { (episodeProvider, "42") };
        var series = seriesProvider.Length == 0 ? Ids(("Imdb", "tt0903747")) : Ids((seriesProvider, "7"));
        var item = ItemMapper.Map(Episode(series, episode));

        Assert.Equal(expected, item?.Numbering);
    }

    [Fact]
    public void Numbering_is_imdb_when_only_imdb_is_known_and_null_for_anime_only_ids()
    {
        Assert.Equal(Numbering.Imdb, ItemMapper.Map(Episode(Ids(("Imdb", "tt0903747"))))?.Numbering);
        Assert.Null(ItemMapper.Map(Episode(Ids(("AniDB", "69"))))?.Numbering);
    }

    [Fact]
    public void Content_key_is_the_same_for_alternate_versions_and_differs_between_episodes()
    {
        var series = Ids(("Tvdb", "81189"));
        var a = ItemMapper.Map(Episode(series, ("Tvdb", "349232")))!;
        var b = ItemMapper.Map(Episode(series, ("Tvdb", "349232")))!;
        var other = ItemMapper.Map(new MediaFacts(MediaKind.Episode, "Cat's in the Bag", "Breaking Bad", 1, 2, Ids(("Tvdb", "349233")), series))!;

        Assert.Equal(ItemMapper.ContentKey(a), ItemMapper.ContentKey(b));
        Assert.NotEqual(ItemMapper.ContentKey(a), ItemMapper.ContentKey(other));
        Assert.StartsWith("m:", ItemMapper.ContentKey(ItemMapper.Map(Movie(("Tmdb", "603")))!), StringComparison.Ordinal);
    }

    [Fact]
    public void Titles_are_trimmed_and_capped()
    {
        var item = ItemMapper.Map(new MediaFacts(MediaKind.Movie, "  " + new string('x', 500) + "  ", null, null, null, Ids(("Tmdb", "1")), null));

        Assert.Equal(300, item!.Title!.Length);
    }

    [Fact]
    public void An_event_serialises_to_the_C1_shape()
    {
        var batch = new ScrobbleBatch(
            Samples.Client,
            [new ScrobbleEvent(EventActions.Watched, DateTimeOffset.Parse("2026-10-02T14:00:00.123+02:00", System.Globalization.CultureInfo.InvariantCulture), "abc", 123000, 1440000, false, Samples.BreakingBadPilot())]);

        var json = JsonSerializer.Serialize(batch, Json.Options);

        const string expected = """
            {"client":{"name":"jellyfin-plugin","version":"1.0.0.0","server":"Jellyfin 12.1"},"events":[{"action":"watched","occurredAt":"2026-10-02T12:00:00Z","sessionId":"abc","positionMs":123000,"durationMs":1440000,"manual":false,"item":{"kind":"episode","title":"Breaking Bad - Pilot","ids":{"imdb":"tt0903747","tmdb":1396,"tvdb":81189},"episodeIds":{"tvdb":349232},"season":1,"episode":1,"numbering":"tvdb"}}]}
            """;
        Assert.Equal(expected, json);
    }

    [Fact]
    public void An_event_survives_a_json_round_trip()
    {
        var original = new ScrobbleEvent(EventActions.Stop, DateTimeOffset.Parse("2026-10-02T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "s1", null, 5000, false, Samples.Matrix());

        var copy = JsonSerializer.Deserialize<ScrobbleEvent>(JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(original, copy);
    }
}

using Jellyfin.Plugin.TofuTracker.Core;
using Jellyfin.Plugin.TofuTracker.Playback;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TofuTracker.Tests;

[Collection(JellyfinStaticsCollection.Name)]
public class JellyfinItemReaderTests
{
    [Fact]
    public void A_movie_is_read_with_its_provider_ids()
    {
        var facts = JellyfinItemReader.Read(JellyfinFixtures.Matrix());

        Assert.NotNull(facts);
        Assert.Equal(MediaKind.Movie, facts.Kind);
        Assert.Equal("The Matrix", facts.Name);
        Assert.Equal("603", facts.ProviderIds["tmdb"]);
        Assert.Null(facts.SeriesProviderIds);
    }

    [Fact]
    public void An_episode_is_read_with_the_series_ids_the_season_and_the_episode_number()
    {
        var facts = JellyfinItemReader.Read(JellyfinFixtures.BreakingBadPilot());

        Assert.NotNull(facts);
        Assert.Equal(MediaKind.Episode, facts.Kind);
        Assert.Equal("Breaking Bad", facts.SeriesName);
        Assert.Equal(1, facts.Season);
        Assert.Equal(1, facts.Episode);
        Assert.Equal("349232", facts.ProviderIds["Tvdb"]);
        Assert.Equal("81189", facts.SeriesProviderIds!["Tvdb"]);
        Assert.Equal("tt0903747", facts.SeriesProviderIds["Imdb"]);
    }

    [Fact]
    public void An_episode_maps_end_to_end_to_series_level_ids_plus_episode_ids()
    {
        var item = ItemMapper.Map(JellyfinItemReader.Read(JellyfinFixtures.BreakingBadPilot())!);

        Assert.Equal(Samples.BreakingBadPilot(), item);
    }

    [Fact]
    public void An_episode_without_a_series_keeps_its_own_ids_only()
    {
        var facts = JellyfinItemReader.Read(JellyfinFixtures.BreakingBadPilot(withSeries: false));

        Assert.NotNull(facts);
        Assert.Null(facts.SeriesProviderIds);
        Assert.Equal("Breaking Bad", facts.SeriesName);
    }

    [Fact]
    public void Theme_media_and_extras_are_ignored()
    {
        var theme = new Movie { Name = "Theme", ExtraType = ExtraType.ThemeVideo };
        var trailer = new Movie { Name = "Trailer", ExtraType = ExtraType.Trailer };

        Assert.Null(JellyfinItemReader.Read(theme));
        Assert.Null(JellyfinItemReader.Read(trailer));
    }

    [Fact]
    public void Music_and_nothing_are_ignored()
    {
        Assert.Null(JellyfinItemReader.Read(new Audio { Name = "Song" }));
        Assert.Null(JellyfinItemReader.Read(null));
    }

    [Fact]
    public void The_facts_are_a_copy_so_later_metadata_changes_do_not_alter_a_queued_event()
    {
        var movie = JellyfinFixtures.Matrix();
        var facts = JellyfinItemReader.Read(movie)!;

        movie.ProviderIds["Tmdb"] = "999";

        Assert.Equal("603", facts.ProviderIds["Tmdb"]);
    }
}

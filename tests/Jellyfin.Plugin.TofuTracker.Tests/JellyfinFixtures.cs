using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using NSubstitute;

namespace Jellyfin.Plugin.TofuTracker.Tests;

/// <summary>Tests that touch Jellyfin's process-wide statics (<c>BaseItem.LibraryManager</c>, <c>Plugin.Instance</c>) run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JellyfinStaticsCollection
{
    public const string Name = "Jellyfin statics";
}

internal static class JellyfinFixtures
{
    public static User NewUser(string name = "alice")
    {
        return new User(name, "auth-provider", "reset-provider") { Id = Guid.NewGuid() };
    }

    public static Movie Matrix()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "The Matrix", ProductionYear = 1999, RunTimeTicks = 136 * 60 * TimeSpan.TicksPerSecond };
        movie.ProviderIds["Imdb"] = "tt0133093";
        movie.ProviderIds["Tmdb"] = "603";
        return movie;
    }

    /// <summary>An episode whose series is served by a fake library manager (as Jellyfin does through <c>Episode.Series</c>).</summary>
    public static Episode BreakingBadPilot(bool withSeries = true)
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Pilot",
            SeriesName = "Breaking Bad",
            ParentIndexNumber = 1,
            IndexNumber = 1,
            RunTimeTicks = 58 * 60 * TimeSpan.TicksPerSecond,
        };
        episode.ProviderIds["Tvdb"] = "349232";

        if (withSeries)
        {
            var series = new Series { Id = Guid.NewGuid(), Name = "Breaking Bad" };
            series.ProviderIds["Imdb"] = "tt0903747";
            series.ProviderIds["Tmdb"] = "1396";
            series.ProviderIds["Tvdb"] = "81189";
            episode.SeriesId = series.Id;

            var libraryManager = Substitute.For<ILibraryManager>();
            libraryManager.GetItemById(series.Id).Returns(series);
            BaseItem.LibraryManager = libraryManager;
        }

        return episode;
    }
}

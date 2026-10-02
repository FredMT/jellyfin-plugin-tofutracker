using Jellyfin.Plugin.TofuTracker.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.TofuTracker.Playback;

/// <summary>The one place that reads Jellyfin entities. Everything after it works on <see cref="MediaFacts"/>.</summary>
public static class JellyfinItemReader
{
    /// <summary>Reads a movie or episode; returns null for anything else (music, books, trailers, theme media).</summary>
    public static MediaFacts? Read(BaseItem? item)
    {
        if (item is null || item.IsThemeMedia || item.ExtraType is not null)
        {
            return null;
        }

        switch (item)
        {
            case Movie movie:
                return new MediaFacts(MediaKind.Movie, movie.Name, null, null, null, Copy(movie.ProviderIds), null);

            case Episode episode:
                var series = episode.Series;
                return new MediaFacts(
                    MediaKind.Episode,
                    episode.Name,
                    string.IsNullOrWhiteSpace(episode.SeriesName) ? series?.Name : episode.SeriesName,
                    episode.ParentIndexNumber,
                    episode.IndexNumber,
                    Copy(episode.ProviderIds),
                    series is null ? null : Copy(series.ProviderIds));

            default:
                return null;
        }
    }

    private static Dictionary<string, string> Copy(Dictionary<string, string>? source)
    {
        return source is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
    }
}

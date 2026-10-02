using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>What the plugin needs to know about a played item, free of Jellyfin types.</summary>
/// <param name="Kind">Movie or episode.</param>
/// <param name="Name">The item's own name.</param>
/// <param name="SeriesName">The series name for an episode.</param>
/// <param name="Season">The episode's season number (<c>ParentIndexNumber</c>).</param>
/// <param name="Episode">The episode's number in its season (<c>IndexNumber</c>).</param>
/// <param name="ProviderIds">The item's own provider ids (for an episode these are episode ids).</param>
/// <param name="SeriesProviderIds">The series' provider ids; null for movies and orphan episodes.</param>
public sealed record MediaFacts(
    MediaKind Kind,
    string? Name,
    string? SeriesName,
    int? Season,
    int? Episode,
    IReadOnlyDictionary<string, string> ProviderIds,
    IReadOnlyDictionary<string, string>? SeriesProviderIds);

public enum MediaKind
{
    Movie,
    Episode,
}

/// <summary>Turns <see cref="MediaFacts"/> into the <c>item</c> block of the client event contract.</summary>
public static partial class ItemMapper
{
    private const int MaxTitleLength = 300;

    [GeneratedRegex(@"^tt\d{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImdbPattern();

    /// <summary>
    /// Maps an item. Returns null when no provider id is known at all: the scrobbler never matches on
    /// the title alone, so such an item could only ever land in the review queue.
    /// </summary>
    public static ScrobbleItem? Map(MediaFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Kind switch
        {
            MediaKind.Movie => MapMovie(facts),
            MediaKind.Episode => MapEpisode(facts),
            _ => null,
        };
    }

    /// <summary>A stable, human readable key of what was played (not of the Jellyfin item), for deduping.</summary>
    public static string ContentKey(ScrobbleItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var title = FirstTitleId(item.Ids) ?? "none";
        if (string.Equals(item.Kind, ItemKinds.Movie, StringComparison.Ordinal))
        {
            return "m:" + title;
        }

        var episodeId = item.EpisodeIds is { IsEmpty: false } ids ? FirstEpisodeId(ids) : null;
        var position = item.Season is { } s && item.Episode is { } e
            ? string.Create(CultureInfo.InvariantCulture, $"s{s}e{e}")
            : "s?e?";
        return $"e:{title}:{position}" + (episodeId is null ? string.Empty : ":" + episodeId);
    }

    /// <summary>Parses a Jellyfin provider id the scrobbler expects as an integer.</summary>
    public static long? ParseNumericId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }

    /// <summary>Parses an IMDb id ("tt0903747"); anything else is dropped.</summary>
    public static string? ParseImdbId(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is not null && ImdbPattern().IsMatch(trimmed) ? trimmed : null;
    }

    private static ScrobbleItem? MapMovie(MediaFacts facts)
    {
        var ids = ReadTitleIds(facts.ProviderIds);
        if (ids.IsEmpty)
        {
            return null;
        }

        return new ScrobbleItem(ItemKinds.Movie, Clip(facts.Name), ids);
    }

    private static ScrobbleItem? MapEpisode(MediaFacts facts)
    {
        var titleIds = facts.SeriesProviderIds is null ? new TitleIds() : ReadTitleIds(facts.SeriesProviderIds);
        var episodeIds = ReadEpisodeIds(facts.ProviderIds);
        if (titleIds.IsEmpty && episodeIds.IsEmpty)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(facts.SeriesName)
            ? facts.Name
            : string.IsNullOrWhiteSpace(facts.Name) ? facts.SeriesName : $"{facts.SeriesName} - {facts.Name}";

        return new ScrobbleItem(
            ItemKinds.Episode,
            Clip(title),
            titleIds,
            episodeIds.IsEmpty ? null : episodeIds,
            facts.Season,
            facts.Episode,
            DetectNumbering(titleIds, episodeIds));
    }

    /// <summary>
    /// Season and episode numbers come from the file names, checked by whichever metadata plugin matched the
    /// episode. An id on the episode itself shows which provider that was; without one the series ids are the
    /// best evidence. TheTVDB wins over TMDb because the scrobbler resolves anime through TVDB numbering.
    /// </summary>
    internal static string? DetectNumbering(TitleIds titleIds, EpisodeIds episodeIds)
    {
        if (episodeIds.Tvdb is not null)
        {
            return Numbering.Tvdb;
        }

        if (episodeIds.Tmdb is not null)
        {
            return Numbering.Tmdb;
        }

        if (titleIds.Tvdb is not null)
        {
            return Numbering.Tvdb;
        }

        if (titleIds.Tmdb is not null)
        {
            return Numbering.Tmdb;
        }

        if (titleIds.Imdb is not null || episodeIds.Imdb is not null)
        {
            return Numbering.Imdb;
        }

        return null;
    }

    private static TitleIds ReadTitleIds(IReadOnlyDictionary<string, string> source)
    {
        return new TitleIds(
            Imdb: ParseImdbId(Lookup(source, "Imdb")),
            Tmdb: ParseNumericId(Lookup(source, "Tmdb")),
            Tvdb: ParseNumericId(Lookup(source, "Tvdb")),
            Anidb: ParseNumericId(Lookup(source, "AniDB")),
            Anilist: ParseNumericId(Lookup(source, "AniList")),
            Mal: ParseNumericId(Lookup(source, "MyAnimeList", "MAL")),
            Kitsu: ParseNumericId(Lookup(source, "Kitsu")));
    }

    private static EpisodeIds ReadEpisodeIds(IReadOnlyDictionary<string, string> source)
    {
        return new EpisodeIds(
            Imdb: ParseImdbId(Lookup(source, "Imdb")),
            Tmdb: ParseNumericId(Lookup(source, "Tmdb")),
            Tvdb: ParseNumericId(Lookup(source, "Tvdb")));
    }

    /// <summary>Jellyfin's own dictionary ignores key case; a copy made elsewhere might not, so compare loosely.</summary>
    private static string? Lookup(IReadOnlyDictionary<string, string> source, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var pair in source)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    private static string? FirstTitleId(TitleIds ids)
    {
        if (ids.Tmdb is { } tmdb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"tmdb{tmdb}");
        }

        if (ids.Tvdb is { } tvdb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"tvdb{tvdb}");
        }

        if (ids.Imdb is { } imdb)
        {
            return imdb;
        }

        if (ids.Mal is { } mal)
        {
            return string.Create(CultureInfo.InvariantCulture, $"mal{mal}");
        }

        if (ids.Anidb is { } anidb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"anidb{anidb}");
        }

        if (ids.Anilist is { } anilist)
        {
            return string.Create(CultureInfo.InvariantCulture, $"anilist{anilist}");
        }

        return ids.Kitsu is { } kitsu ? string.Create(CultureInfo.InvariantCulture, $"kitsu{kitsu}") : null;
    }

    private static string? FirstEpisodeId(EpisodeIds ids)
    {
        if (ids.Tvdb is { } tvdb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"tvdb{tvdb}");
        }

        if (ids.Tmdb is { } tmdb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"tmdb{tmdb}");
        }

        return ids.Imdb;
    }

    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= MaxTitleLength ? trimmed : trimmed[..MaxTitleLength];
    }
}

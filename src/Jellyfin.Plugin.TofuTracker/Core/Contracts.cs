using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>Event actions of the client event contract (C1).</summary>
public static class EventActions
{
    public const string Start = "start";
    public const string Progress = "progress";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Watched = "watched";
}

/// <summary>Item kinds of the client event contract (C1).</summary>
public static class ItemKinds
{
    public const string Movie = "movie";
    public const string Episode = "episode";
}

/// <summary>Season numbering schemes of the client event contract (C1).</summary>
public static class Numbering
{
    public const string Tvdb = "tvdb";
    public const string Tmdb = "tmdb";
    public const string Imdb = "imdb";
}

/// <summary>Shared JSON settings for everything sent to or stored from the scrobbler.</summary>
public static class Json
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>The <c>client</c> block of a batch.</summary>
public sealed record ClientInfo(string Name, string Version, string Server);

/// <summary>Title level ids: the movie, or the series of an episode.</summary>
public sealed record TitleIds(
    string? Imdb = null,
    long? Tmdb = null,
    long? Tvdb = null,
    long? Anidb = null,
    long? Anilist = null,
    long? Mal = null,
    long? Kitsu = null)
{
    [JsonIgnore]
    public bool IsEmpty => Imdb is null && Tmdb is null && Tvdb is null && Anidb is null
        && Anilist is null && Mal is null && Kitsu is null;
}

/// <summary>The episode's own ids.</summary>
public sealed record EpisodeIds(string? Imdb = null, long? Tmdb = null, long? Tvdb = null)
{
    [JsonIgnore]
    public bool IsEmpty => Imdb is null && Tmdb is null && Tvdb is null;
}

/// <summary>The <c>item</c> block of an event.</summary>
public sealed record ScrobbleItem(
    string Kind,
    string? Title,
    TitleIds Ids,
    EpisodeIds? EpisodeIds = null,
    int? Season = null,
    int? Episode = null,
    string? Numbering = null);

/// <summary>One event of a batch.</summary>
public sealed record ScrobbleEvent(
    string Action,
    [property: JsonConverter(typeof(EventTimeConverter))] DateTimeOffset OccurredAt,
    string SessionId,
    long? PositionMs,
    long? DurationMs,
    bool Manual,
    ScrobbleItem Item);

/// <summary>The request body of <c>POST /v1/events</c>.</summary>
public sealed record ScrobbleBatch(ClientInfo Client, IReadOnlyList<ScrobbleEvent> Events);

/// <summary>Writes <c>2026-10-02T14:00:00Z</c> (UTC, whole seconds) and reads any ISO 8601 timestamp.</summary>
public sealed class EventTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("Timestamp is null.");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

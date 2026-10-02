using System.Net;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.TofuTracker.Core;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TofuTracker.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tofutracker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? UserAgent, string Body)
{
    public JsonDocument Json => JsonDocument.Parse(Body);

    public JsonElement Events => Json.RootElement.GetProperty("events");
}

/// <summary>An HttpMessageHandler that records requests and answers from a delegate.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Accepted);

    public HttpClient CreateClient() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.UserAgent.ToString(),
            body);
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return Respond(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>A plain XmlSerializer behind Jellyfin's serializer interface, enough for a plugin to load and save its settings.</summary>
internal sealed class TestXmlSerializer : IXmlSerializer
{
    public object DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream)!;

    public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

    public void SerializeToFile(object obj, string file)
    {
        using var stream = File.Create(file);
        SerializeToStream(obj, stream);
    }

    public object DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer);
        return DeserializeFromStream(type, stream);
    }
}

internal static class Samples
{
    public static ILogger Logger { get; } = NullLogger.Instance;

    public static ClientInfo Client { get; } = new("jellyfin-plugin", "1.0.0.0", "Jellyfin 12.1");

    public static ScrobbleItem BreakingBadPilot() => new(
        ItemKinds.Episode,
        "Breaking Bad - Pilot",
        new TitleIds(Imdb: "tt0903747", Tmdb: 1396, Tvdb: 81189),
        new EpisodeIds(Tvdb: 349232),
        1,
        1,
        Numbering.Tvdb);

    public static ScrobbleItem Matrix() => new(
        ItemKinds.Movie,
        "The Matrix",
        new TitleIds(Imdb: "tt0133093", Tmdb: 603));

    public static ScrobbleEvent Event(string action, string sessionId, DateTimeOffset at, ScrobbleItem? item = null, bool manual = false)
    {
        return new ScrobbleEvent(action, at, sessionId, 1000, 60000, manual, item ?? Matrix());
    }

    public static UserLink Link(Guid userId, string token = "tok-secret-1", string username = "kalugu")
    {
        return new UserLink(userId, "conn-" + userId.ToString("N")[..6], token, username, DateTimeOffset.Parse("2026-10-02T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }
}

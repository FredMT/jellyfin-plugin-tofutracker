using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TofuTracker.Core;

/// <summary>A Jellyfin user linked to a TofuTracker account. The token is a secret and never leaves this process.</summary>
public sealed record UserLink(
    Guid JellyfinUserId,
    string ConnectionId,
    string Token,
    string Username,
    DateTimeOffset LinkedAt,
    bool Broken = false,
    DateTimeOffset? BrokenAt = null);

/// <summary>
/// The per-user connection tokens. They live in their own file instead of the plugin configuration because
/// Jellyfin returns the whole configuration to any admin API call, and the tokens must never reach a page.
/// </summary>
public sealed class LinkStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, UserLink> _links = [];

    public LinkStore(string directory, ILogger logger)
    {
        _path = Path.Combine(directory, "links.json");
        _logger = logger;
        Load();
    }

    public IReadOnlyList<UserLink> All()
    {
        lock (_gate)
        {
            return [.. _links.Values];
        }
    }

    public UserLink? Get(Guid userId)
    {
        lock (_gate)
        {
            return _links.GetValueOrDefault(userId);
        }
    }

    /// <summary>The link of a user whose token still works, or null.</summary>
    public UserLink? GetActive(Guid userId)
    {
        var link = Get(userId);
        return link is { Broken: false } ? link : null;
    }

    public void Upsert(UserLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (_gate)
        {
            _links[link.JellyfinUserId] = link;
            Save();
        }
    }

    public bool Remove(Guid userId)
    {
        lock (_gate)
        {
            if (!_links.Remove(userId))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>The scrobbler answered 401: the token was revoked or is unknown. Stop sending until relinked.</summary>
    public void MarkBroken(Guid userId, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_links.TryGetValue(userId, out var link) && !link.Broken)
            {
                _links[userId] = link with { Broken = true, BrokenAt = at };
                Save();
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<LinkFile>(File.ReadAllText(_path), Json.Options);
            foreach (var link in file?.Links ?? [])
            {
                _links[link.JellyfinUserId] = link;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var aside = _path + ".corrupt";
            _logger.LogError(ex, "TofuTracker could not read {Path}; moving it to {Aside}. Users must be linked again.", _path, aside);
            try
            {
                File.Move(_path, aside, overwrite: true);
            }
            catch (IOException)
            {
                // Nothing more to do; the next save replaces the file.
            }
        }
    }

    private void Save()
    {
        var file = new LinkFile(1, [.. _links.Values]);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, Json.Options), secret: true);
    }

    private sealed record LinkFile(int Version, List<UserLink> Links);
}

/// <summary>Writes a file through a temp file and rename, so a crash never leaves half a file behind.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, bool secret)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (secret && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}

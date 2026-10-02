using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TofuTracker.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TofuTracker.Api;

/// <summary>
/// Endpoints behind the plugin's admin page. Admin only. They return link state and pairing codes but never a
/// token: the token goes from the scrobbler straight into the server-side <see cref="LinkStore"/>.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("TofuTracker")]
[Produces(MediaTypeNames.Application.Json)]
public class TofuTrackerController : ControllerBase
{
    private readonly IUserManager _users;
    private readonly IServerApplicationHost _host;
    private readonly LinkStore _links;
    private readonly PairingCoordinator _pairing;
    private readonly ScrobbleSender _sender;
    private readonly SkippedItemLog _skipped;

    public TofuTrackerController(
        IUserManager users,
        IServerApplicationHost host,
        LinkStore links,
        PairingCoordinator pairing,
        ScrobbleSender sender,
        SkippedItemLog skipped)
    {
        _users = users;
        _host = host;
        _links = links;
        _pairing = pairing;
        _sender = sender;
        _skipped = skipped;
    }

    /// <summary>Gets the server URL, every Jellyfin user with their link and pairing state, and the sender's health.</summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusDto> GetStatus()
    {
        var users = _users.GetUsers()
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => ToDto(u.Id, u.Username))
            .ToList();

        return new StatusDto
        {
            ServerUrl = ScrobblerUrl.NormalizeOrDefault(Plugin.Instance?.Configuration.ServerUrl),
            DefaultServerUrl = ScrobblerUrl.Default,
            Users = users,
            PendingEvents = _sender.PendingCount,
            LastSuccessAt = _sender.LastSuccessAt,
            LastError = _sender.LastError,
            NotSent = [.. _skipped.Recent().Select(i => new SkippedItemDto { Name = i.Name, Reason = i.Reason, At = i.At })],
        };
    }

    /// <summary>Saves the scrobbler URL.</summary>
    [HttpPost("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SettingsDto> SaveSettings([FromBody] SettingsDto settings)
    {
        if (Plugin.Instance is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new MessageDto("The plugin is not ready."));
        }

        if (!ScrobblerUrl.TryNormalize(settings.ServerUrl, out var normalized))
        {
            return BadRequest(new MessageDto("Enter an https:// address (http:// is only allowed for localhost)."));
        }

        Plugin.Instance.Configuration.ServerUrl = normalized;
        Plugin.Instance.SaveConfiguration();
        return new SettingsDto { ServerUrl = normalized };
    }

    /// <summary>Starts linking a Jellyfin user: returns the code the TofuTracker user has to approve.</summary>
    [HttpPost("Link/Start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<UserDto>> StartLink([FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        var user = _users.GetUserById(request.UserId);
        if (user is null)
        {
            return NotFound(new MessageDto("No such Jellyfin user."));
        }

        try
        {
            await _pairing.StartAsync(user.Id, $"{_host.FriendlyName} / {user.Username}", cancellationToken).ConfigureAwait(false);
        }
        catch (PairingException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new MessageDto(ex.Message));
        }

        return ToDto(user.Id, user.Username);
    }

    /// <summary>Gets one user's link and pairing state.</summary>
    [HttpGet("Link/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserDto> GetLinkStatus([FromQuery] Guid userId)
    {
        var user = _users.GetUserById(userId);
        return user is null ? NotFound(new MessageDto("No such Jellyfin user.")) : ToDto(user.Id, user.Username);
    }

    /// <summary>Stops a pending pairing.</summary>
    [HttpPost("Link/Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult CancelLink([FromBody] UserRequest request)
    {
        _pairing.Cancel(request.UserId);
        return NoContent();
    }

    /// <summary>Forgets a user's token on this server. It does not revoke it on tofutracker.com.</summary>
    [HttpPost("Unlink")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Unlink([FromBody] UserRequest request)
    {
        _pairing.Cancel(request.UserId);
        _links.Remove(request.UserId);
        return NoContent();
    }

    private UserDto ToDto(Guid userId, string username)
    {
        var link = _links.Get(userId);
        var pairing = _pairing.Status(userId);
        return new UserDto
        {
            Id = userId,
            Name = username,
            Linked = link is not null,
            LinkBroken = link?.Broken ?? false,
            TofuTrackerUsername = link?.Username,
            LinkedAt = link?.LinkedAt,
            Pairing = pairing.State == PairingState.None
                ? null
                : new PairingDto
                {
                    State = pairing.State.ToString().ToLowerInvariant(),
                    UserCode = pairing.UserCode,
                    VerificationUrl = pairing.VerificationUrl,
                    ExpiresAt = pairing.ExpiresAt,
                    Message = pairing.Message,
                },
        };
    }

    /// <summary>Body of requests that name a Jellyfin user.</summary>
    public sealed class UserRequest
    {
        [JsonPropertyName("userId")]
        public Guid UserId { get; set; }
    }

    /// <summary>The scrobbler URL.</summary>
    public sealed class SettingsDto
    {
        [JsonPropertyName("serverUrl")]
        public string? ServerUrl { get; set; }
    }

    /// <summary>An error the page shows as is.</summary>
    public sealed record MessageDto([property: JsonPropertyName("message")] string Message);

    /// <summary>Everything the page needs to draw itself.</summary>
    public sealed class StatusDto
    {
        [JsonPropertyName("serverUrl")]
        public string ServerUrl { get; set; } = string.Empty;

        [JsonPropertyName("defaultServerUrl")]
        public string DefaultServerUrl { get; set; } = string.Empty;

        [JsonPropertyName("users")]
        public IReadOnlyList<UserDto> Users { get; set; } = [];

        [JsonPropertyName("pendingEvents")]
        public int PendingEvents { get; set; }

        [JsonPropertyName("lastSuccessAt")]
        public DateTimeOffset? LastSuccessAt { get; set; }

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }

        [JsonPropertyName("notSent")]
        public IReadOnlyList<SkippedItemDto> NotSent { get; set; } = [];
    }

    /// <summary>An item that was played but could not be sent, and why.</summary>
    public sealed class SkippedItemDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("at")]
        public DateTimeOffset At { get; set; }
    }

    /// <summary>One Jellyfin user and whether they are linked.</summary>
    public sealed class UserDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("linked")]
        public bool Linked { get; set; }

        [JsonPropertyName("linkBroken")]
        public bool LinkBroken { get; set; }

        [JsonPropertyName("tofuTrackerUsername")]
        public string? TofuTrackerUsername { get; set; }

        [JsonPropertyName("linkedAt")]
        public DateTimeOffset? LinkedAt { get; set; }

        [JsonPropertyName("pairing")]
        public PairingDto? Pairing { get; set; }
    }

    /// <summary>A pairing in progress or just finished. Never carries the device code or the token.</summary>
    public sealed class PairingDto
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("userCode")]
        public string? UserCode { get; set; }

        [JsonPropertyName("verificationUrl")]
        public string? VerificationUrl { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

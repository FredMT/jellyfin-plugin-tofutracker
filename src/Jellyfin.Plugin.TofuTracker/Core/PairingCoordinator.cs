using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TofuTracker.Core;

public enum PairingState
{
    /// <summary>No pairing is running for this user.</summary>
    None,
    Pending,
    Approved,
    Denied,
    Expired,
    Failed,
}

/// <summary>What the admin page may see of a pairing. The device code and the token stay in this process.</summary>
public sealed record PairingView(
    PairingState State,
    string? UserCode,
    string? VerificationUrl,
    DateTimeOffset? ExpiresAt,
    string? Username,
    string? Message);

public sealed class PairingException : Exception
{
    public PairingException(string message)
        : base(message)
    {
    }

    public PairingException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Drives the device-code flow (C2) for Jellyfin users: <c>/v1/pair/start</c> shows a code, then a background
/// loop calls <c>/v1/pair/poll</c> until the TofuTracker user approves it. The token is stored in the
/// <see cref="LinkStore"/> and never returned to callers, so the admin page cannot read it.
/// </summary>
public sealed class PairingCoordinator
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<HttpClient> _http;
    private readonly Func<string> _serverUrl;
    private readonly Func<ClientInfo> _client;
    private readonly LinkStore _links;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Pairing> _pairings = [];

    public PairingCoordinator(
        Func<HttpClient> http,
        Func<string> serverUrl,
        Func<ClientInfo> client,
        LinkStore links,
        TimeProvider time,
        ILogger logger)
    {
        _http = http;
        _serverUrl = serverUrl;
        _client = client;
        _links = links;
        _time = time;
        _logger = logger;
    }

    /// <summary>Starts (or restarts) a pairing for a user and polls for approval in the background.</summary>
    public async Task<PairingView> StartAsync(Guid userId, string label, CancellationToken cancellationToken)
    {
        var response = await PostAsync<StartResponse>(
            "v1/pair/start",
            new { adapter = "jellyfin", label = Clip(label, 100) },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response.DeviceCode) || string.IsNullOrWhiteSpace(response.UserCode))
        {
            throw new PairingException("TofuTracker answered without a device code.");
        }

        var interval = TimeSpan.FromSeconds(Math.Max(response.Interval, MinInterval.TotalSeconds));
        var pairing = new Pairing(
            response.DeviceCode,
            response.UserCode,
            response.VerificationUrl ?? string.Empty,
            interval,
            _time.GetUtcNow() + TimeSpan.FromSeconds(response.ExpiresIn > 0 ? response.ExpiresIn : 600));

        Pairing? previous;
        lock (_gate)
        {
            _pairings.TryGetValue(userId, out previous);
            _pairings[userId] = pairing;
        }

        previous?.Cancel();
        pairing.Loop = Task.Run(() => PollLoopAsync(userId, pairing), CancellationToken.None);
        return View(pairing);
    }

    public PairingView Status(Guid userId)
    {
        lock (_gate)
        {
            return _pairings.TryGetValue(userId, out var pairing)
                ? View(pairing)
                : new PairingView(PairingState.None, null, null, null, null, null);
        }
    }

    public void Cancel(Guid userId)
    {
        Pairing? pairing;
        lock (_gate)
        {
            _pairings.Remove(userId, out pairing);
        }

        pairing?.Cancel();
    }

    /// <summary>Stops every background poll (plugin shutdown).</summary>
    public void CancelAll()
    {
        List<Pairing> all;
        lock (_gate)
        {
            all = [.. _pairings.Values];
            _pairings.Clear();
        }

        foreach (var pairing in all)
        {
            pairing.Cancel();
        }
    }

    /// <summary>One poll of a pending pairing. Public so tests can step the flow without waiting.</summary>
    public async Task<PairingView> PollOnceAsync(Guid userId, CancellationToken cancellationToken)
    {
        Pairing? pairing;
        lock (_gate)
        {
            _pairings.TryGetValue(userId, out pairing);
        }

        if (pairing is null)
        {
            return new PairingView(PairingState.None, null, null, null, null, null);
        }

        if (pairing.State != PairingState.Pending)
        {
            return View(pairing);
        }

        if (_time.GetUtcNow() >= pairing.ExpiresAt)
        {
            pairing.Finish(PairingState.Expired, null, "The code expired. Start again.");
            return View(pairing);
        }

        PollResponse response;
        try
        {
            response = await PostAsync<PollResponse>("v1/pair/poll", new { deviceCode = pairing.DeviceCode }, cancellationToken).ConfigureAwait(false);
        }
        catch (PairingException ex)
        {
            // Keep polling: a network blip must not end a pairing the user is about to approve.
            pairing.Message = ex.Message;
            return View(pairing);
        }

        pairing.Message = null;
        switch (response.Status)
        {
            case "approved":
                if (string.IsNullOrWhiteSpace(response.Token) || string.IsNullOrWhiteSpace(response.ConnectionId))
                {
                    pairing.Finish(PairingState.Failed, null, "TofuTracker approved the link but sent no token.");
                    break;
                }

                _links.Upsert(new UserLink(
                    userId,
                    response.ConnectionId,
                    response.Token,
                    response.Username ?? string.Empty,
                    _time.GetUtcNow()));
                _logger.LogInformation("Jellyfin user {UserId} was linked to TofuTracker account {Username}.", userId, response.Username);
                pairing.Finish(PairingState.Approved, response.Username, null);
                break;

            case "denied":
                pairing.Finish(PairingState.Denied, null, "The link request was denied.");
                break;

            case "expired":
                pairing.Finish(PairingState.Expired, null, "The code expired. Start again.");
                break;

            default:
                break;
        }

        return View(pairing);
    }

    private async Task PollLoopAsync(Guid userId, Pairing pairing)
    {
        var token = pairing.Cancellation.Token;
        try
        {
            while (pairing.State == PairingState.Pending)
            {
                await Task.Delay(pairing.Interval, _time, token).ConfigureAwait(false);
                await PollOnceAsync(userId, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled or replaced.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TofuTracker pairing loop for user {UserId} failed.", userId);
            pairing.Finish(PairingState.Failed, null, "Unexpected error; see the Jellyfin log.");
        }
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ScrobblerUrl.Combine(_serverUrl(), path))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TofuTracker-Jellyfin-Plugin", _client().Version));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http().SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new PairingException("TofuTracker asked us to slow down; try again in a minute.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PairingException($"TofuTracker answered HTTP {(int)response.StatusCode}.");
            }

            var parsed = await response.Content.ReadFromJsonAsync<T>(Json.Options, timeout.Token).ConfigureAwait(false);
            return parsed ?? throw new PairingException("TofuTracker sent an empty answer.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PairingException("TofuTracker did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new PairingException("Could not reach TofuTracker: " + (ex.InnerException?.Message ?? ex.Message), ex);
        }
        catch (JsonException ex)
        {
            throw new PairingException("TofuTracker sent an answer this plugin does not understand.", ex);
        }
    }

    private static PairingView View(Pairing pairing)
    {
        return new PairingView(
            pairing.State,
            pairing.UserCode,
            pairing.VerificationUrl,
            pairing.ExpiresAt,
            pairing.Username,
            pairing.Message);
    }

    private static string Clip(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    private sealed class Pairing
    {
        public Pairing(string deviceCode, string userCode, string verificationUrl, TimeSpan interval, DateTimeOffset expiresAt)
        {
            DeviceCode = deviceCode;
            UserCode = userCode;
            VerificationUrl = verificationUrl;
            Interval = interval;
            ExpiresAt = expiresAt;
        }

        public string DeviceCode { get; }

        public string UserCode { get; }

        public string VerificationUrl { get; }

        public TimeSpan Interval { get; }

        public DateTimeOffset ExpiresAt { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        private volatile PairingState _state = PairingState.Pending;

        public Task? Loop { get; set; }

        public PairingState State => _state;

        public string? Username { get; private set; }

        public string? Message { get; set; }

        public void Finish(PairingState state, string? username, string? message)
        {
            Username = username;
            Message = message;
            _state = state;
        }

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }
    }

    private sealed record StartResponse(
        string? DeviceCode,
        string? UserCode,
        string? VerificationUrl,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);

    private sealed record PollResponse(string? Status, string? Token, string? ConnectionId, string? Username);
}

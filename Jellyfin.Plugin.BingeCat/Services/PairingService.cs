using System.Collections.Concurrent;
using Jellyfin.Plugin.BingeCat.Api;
using Jellyfin.Plugin.BingeCat.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BingeCat.Services;

/// <summary>Coordinates short-lived device codes without exposing device tokens to the browser.</summary>
public sealed class PairingService
{
    private readonly BingeCatClient _client;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<PairingService> _logger;
    private readonly ConcurrentDictionary<Guid, PendingPairing> _pending = new();

    public PairingService(
        BingeCatClient client,
        IServerApplicationHost applicationHost,
        ILogger<PairingService> logger)
    {
        _client = client;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    public async Task<DeviceCodeResponse> StartAsync(Guid userId, string userName, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("BingeCat plugin configuration is unavailable.");
        var response = await _client.RequestDeviceCodeAsync(new DeviceCodeRequest
        {
            PluginInstanceUuid = configuration.PluginInstanceUuid,
            ServerId = _applicationHost.SystemId,
            ServerName = "Jellyfin",
            ServerVersion = _applicationHost.ApplicationVersionString,
            RemoteUserId = userId.ToString("D"),
            RemoteUserName = userName
        }, cancellationToken).ConfigureAwait(false);
        _pending[userId] = new PendingPairing(response.DeviceCode, userName, DateTime.UtcNow.AddSeconds(response.ExpiresIn));
        return response;
    }

    public async Task<PairingPollResult> PollAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(userId, out var pending))
        {
            return new PairingPollResult("no_pending_code", null);
        }

        if (pending.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _pending.TryRemove(userId, out _);
            return new PairingPollResult("device_code_expired", null);
        }

        var response = await _client.PollDeviceTokenAsync(pending.DeviceCode, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response.AccessToken))
        {
            return new PairingPollResult(response.Error.Length > 0 ? response.Error : "authorization_pending", null);
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("BingeCat plugin is unavailable.");
        var configuration = plugin.Configuration;
        lock (configuration.ProfileLinksSyncRoot)
        {
            var link = configuration.FindLink(userId);
            if (link is null)
            {
                link = new ProfileLink { JellyfinUserId = userId.ToString("D") };
                configuration.ProfileLinks.Add(link);
            }

            link.JellyfinUserName = pending.UserName;
            link.DeviceToken = response.AccessToken;
            link.ConnectionId = response.ConnectionId;
            link.LinkedAtUtc = DateTime.UtcNow;
            link.LastErrorCode = string.Empty;
            plugin.SaveConfiguration();
        }

        _pending.TryRemove(userId, out _);
        _logger.LogInformation("Paired Jellyfin user {UserId} with BingeCat connection {ConnectionId}", userId, response.ConnectionId);
        return new PairingPollResult("connected", response.ConnectionId);
    }

    public void Cancel(Guid userId)
    {
        _pending.TryRemove(userId, out _);
    }

    public Task RevokeAsync(ProfileLink link, CancellationToken cancellationToken)
    {
        return _client.RevokeAsync(link, cancellationToken);
    }

    private sealed record PendingPairing(string DeviceCode, string UserName, DateTime ExpiresAtUtc);
}

public sealed record PairingPollResult(string Status, string? ConnectionId);

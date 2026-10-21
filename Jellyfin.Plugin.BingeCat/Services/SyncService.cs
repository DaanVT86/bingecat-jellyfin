using Jellyfin.Plugin.BingeCat.Api;
using Jellyfin.Plugin.BingeCat.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BingeCat.Services;

/// <summary>Durable outbound delivery loop and authoritative snapshot publisher.</summary>
public sealed class SyncService : BackgroundService
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly BingeCatClient _client;
    private readonly OutboxStore _outbox;
    private readonly RemoteItemFactory _itemFactory;
    private readonly PairingService _pairing;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        BingeCatClient client,
        OutboxStore outbox,
        RemoteItemFactory itemFactory,
        PairingService pairing,
        ILogger<SyncService> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _client = client;
        _outbox = outbox;
        _itemFactory = itemFactory;
        _pairing = pairing;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _outbox.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _userDataManager.UserDataSaved += OnUserDataSaved;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // An escaping exception would stop the whole Jellyfin host (BackgroundServiceExceptionBehavior.StopHost).
                _logger.LogWarning(exception, "BingeCat sync pass failed");
            }

            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        try
        {
            // Progress ticks fire every few seconds during playback; start/stop/played/rating changes carry the state.
            if (eventArgs.SaveReason == UserDataSaveReason.PlaybackProgress)
            {
                return;
            }

            var configuration = Plugin.Instance?.Configuration;
            if (configuration is null || !configuration.ServiceEnabled || eventArgs.Item.IsThemeMedia)
            {
                return;
            }

            var item = _itemFactory.Create(eventArgs.Item, eventArgs.UserData);
            if (item is null)
            {
                return;
            }

            var gate = _outbox.UserGate(eventArgs.UserId);
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (configuration.FindLink(eventArgs.UserId) is null)
                {
                    return;
                }

                await _outbox.EnqueueAsync(eventArgs.UserId, new HistoryEvent
                {
                    EventType = "history.updated",
                    OccurredAt = DateTime.UtcNow,
                    Item = item
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not persist Jellyfin user-data event for BingeCat");
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.ServiceEnabled)
        {
            return;
        }

        ProfileLink[] links;
        lock (configuration.ProfileLinksSyncRoot)
        {
            links = configuration.ProfileLinks
                .Where(link => !string.IsNullOrWhiteSpace(link.DeviceToken))
                .Select(link => link.Copy())
                .ToArray();
        }
        foreach (var link in links)
        {
            if (!Guid.TryParse(link.JellyfinUserId, out var userId) || _pairing.IsPairing(userId))
            {
                continue;
            }

            try
            {
                await DrainOutboxAsync(userId, link, stoppingToken).ConfigureAwait(false);
                var heartbeat = await _client.HeartbeatAsync(link, stoppingToken).ConfigureAwait(false);
                link.LastHeartbeatAtUtc = DateTime.UtcNow;
                if (link.LastErrorCode != "snapshot_too_large")
                {
                    link.LastErrorCode = string.Empty;
                }

                // Snapshot completion is asynchronous (202): BingeCat acknowledges the generation once
                // the upload is applied. A failure acknowledges it too, with the connection in "error".
                if (heartbeat.Connection is { } connection
                    && connection.AcknowledgedSyncGeneration > link.AcknowledgedSyncGeneration)
                {
                    link.AcknowledgedSyncGeneration = connection.AcknowledgedSyncGeneration;
                    if (connection.Status != "error")
                    {
                        link.LastSyncAtUtc = DateTime.UtcNow;
                        link.LastErrorCode = string.Empty;
                    }
                }

                if (heartbeat.SyncCommand is { Type: "full_snapshot" } command)
                {
                    await PublishSnapshotAsync(userId, link, command, stoppingToken).ConfigureAwait(false);
                }

                SaveLinkState(link);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // HttpClient time-outs are OperationCanceledExceptions too: transient, not a shutdown.
                if (link.LastErrorCode != "snapshot_too_large")
                {
                    link.LastErrorCode = exception is HttpRequestException http
                        ? $"http_{(int?)http.StatusCode ?? 0}"
                        : exception is OperationCanceledException ? "timeout" : "sync_failed";
                }
                SaveLinkState(link);
                _logger.LogWarning(exception, "BingeCat sync failed for Jellyfin user {UserId}", userId);
            }
        }
    }

    private async Task DrainOutboxAsync(Guid userId, ProfileLink link, CancellationToken stoppingToken)
    {
        var gate = _outbox.UserGate(userId);
        await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            // The link copy may predate a re-pair or disconnect: never send this user's events
            // with a token that is no longer the current one.
            var current = Plugin.Instance?.Configuration.FindLink(userId);
            if (current is null || !string.Equals(current.DeviceToken, link.DeviceToken, StringComparison.Ordinal))
            {
                return;
            }

            var batch = await _outbox.ReadBatchAsync(userId, 100, stoppingToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                var response = await _client.SendEventsAsync(link, batch.Select(entry => entry.Event).ToArray(), stoppingToken).ConfigureAwait(false);
                await _outbox.DeleteAsync(response.Accepted.Concat(response.Duplicates), stoppingToken).ConfigureAwait(false);
                await _outbox.DeadLetterAsync(userId, response.Rejected, batch, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                await _outbox.DeferAsync(batch, exception.Message, stoppingToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PublishSnapshotAsync(
        Guid userId,
        ProfileLink link,
        SyncCommand command,
        CancellationToken stoppingToken)
    {
        var user = _userManager.GetUserById(userId)
            ?? throw new InvalidOperationException("The paired Jellyfin user no longer exists.");
        var query = new InternalItemsQuery
        {
            User = user,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false
        };
        // Count before materialising so an oversized library is rejected without loading it.
        // ponytail: counts all movies/episodes, including ones the factory later skips, so a
        // library just over the limit with unaddressable episodes is rejected conservatively.
        var maxItems = Math.Max(1, command.MaxItems);
        var libraryCount = _libraryManager.GetCount(query);
        if (libraryCount > maxItems)
        {
            // false = BingeCat is still applying the previous snapshot: busy, retry next tick.
            if (await _client.ReportSnapshotFailureAsync(
                link,
                command.Generation,
                "snapshot_too_large",
                $"The Jellyfin library contains {libraryCount} movies and episodes; limit is {maxItems}.",
                stoppingToken).ConfigureAwait(false))
            {
                link.LastErrorCode = "snapshot_too_large";
            }

            return;
        }

        var items = _libraryManager.GetItemList(query)
            .OrderBy(item => item.Id)
            .Select(item => (_item: item, _userData: _userDataManager.GetUserData(user, item)))
            .Where(value => value._userData is not null)
            .Select(value => _itemFactory.Create(value._item, value._userData!))
            .Where(item => item is not null)
            .Cast<RemoteHistoryItem>()
            .ToArray();

        var runId = await _client.StartSnapshotAsync(link, command.Generation, stoppingToken).ConfigureAwait(false);
        if (runId is null)
        {
            return; // Previous snapshot still being applied; the next heartbeat decides again.
        }

        var pageSize = Math.Clamp(command.PageSize, 25, 1_000);
        var pageCount = 0;
        var rollingChecksum = string.Empty;
        for (var offset = 0; offset < items.Length; offset += pageSize)
        {
            var page = items.Skip(offset).Take(pageSize).ToArray();
            rollingChecksum = await _client.SendSnapshotPageAsync(link, runId, pageCount, page, stoppingToken).ConfigureAwait(false);
            pageCount++;
        }

        // 202: queued for projection. LastSyncAtUtc is set once a heartbeat shows the generation acknowledged.
        await _client.CompleteSnapshotAsync(link, runId, pageCount, items.Length, rollingChecksum, stoppingToken).ConfigureAwait(false);
    }

    private static void SaveLinkState(ProfileLink snapshot)
    {
        var plugin = Plugin.Instance;
        var configuration = plugin?.Configuration;
        if (plugin is null || configuration is null)
        {
            return;
        }

        lock (configuration.ProfileLinksSyncRoot)
        {
            var current = configuration.ProfileLinks.FirstOrDefault(link =>
                string.Equals(link.JellyfinUserId, snapshot.JellyfinUserId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(link.DeviceToken, snapshot.DeviceToken, StringComparison.Ordinal));
            if (current is null)
            {
                return;
            }

            current.LastHeartbeatAtUtc = snapshot.LastHeartbeatAtUtc;
            current.LastSyncAtUtc = snapshot.LastSyncAtUtc;
            current.LastErrorCode = snapshot.LastErrorCode;
            current.AcknowledgedSyncGeneration = snapshot.AcknowledgedSyncGeneration;
            plugin.SaveConfiguration();
        }
    }
}

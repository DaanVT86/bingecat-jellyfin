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
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        BingeCatClient client,
        OutboxStore outbox,
        RemoteItemFactory itemFactory,
        ILogger<SyncService> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _client = client;
        _outbox = outbox;
        _itemFactory = itemFactory;
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
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        try
        {
            var configuration = Plugin.Instance?.Configuration;
            var link = configuration?.FindLink(eventArgs.UserId);
            if (configuration is null || !configuration.ServiceEnabled || link is null || eventArgs.Item.IsThemeMedia)
            {
                return;
            }

            var item = _itemFactory.Create(eventArgs.Item, eventArgs.UserData);
            if (item is null)
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
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not persist Jellyfin user-data event for BingeCat");
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
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
            if (!Guid.TryParse(link.JellyfinUserId, out var userId))
            {
                continue;
            }

            try
            {
                await DrainOutboxAsync(userId, link, cancellationToken).ConfigureAwait(false);
                var heartbeat = await _client.HeartbeatAsync(link, cancellationToken).ConfigureAwait(false);
                link.LastHeartbeatAtUtc = DateTime.UtcNow;
                if (link.LastErrorCode != "snapshot_too_large")
                {
                    link.LastErrorCode = string.Empty;
                }
                if (heartbeat.SyncCommand is { Type: "full_snapshot" } command)
                {
                    await PublishSnapshotAsync(userId, link, command, cancellationToken).ConfigureAwait(false);
                }

                SaveLinkState(link);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (link.LastErrorCode != "snapshot_too_large")
                {
                    link.LastErrorCode = exception is HttpRequestException http
                        ? $"http_{(int?)http.StatusCode ?? 0}"
                        : "sync_failed";
                }
                SaveLinkState(link);
                _logger.LogWarning(exception, "BingeCat sync failed for Jellyfin user {UserId}", userId);
            }
        }
    }

    private async Task DrainOutboxAsync(Guid userId, ProfileLink link, CancellationToken cancellationToken)
    {
        var batch = await _outbox.ReadBatchAsync(userId, 100, cancellationToken).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            var response = await _client.SendEventsAsync(link, batch.Select(entry => entry.Event).ToArray(), cancellationToken).ConfigureAwait(false);
            await _outbox.DeleteAsync(response.Accepted.Concat(response.Duplicates), cancellationToken).ConfigureAwait(false);
            await _outbox.DeadLetterAsync(userId, response.Rejected, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _outbox.DeferAsync(batch, exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task PublishSnapshotAsync(
        Guid userId,
        ProfileLink link,
        SyncCommand command,
        CancellationToken cancellationToken)
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
        var items = _libraryManager.GetItemList(query)
            .Select(item => (_item: item, _userData: _userDataManager.GetUserData(user, item)))
            .Where(value => value._userData is not null)
            .Select(value => _itemFactory.Create(value._item, value._userData!))
            .Where(item => item is not null)
            .Cast<RemoteHistoryItem>()
            .ToArray();

        var maxItems = Math.Max(1, command.MaxItems);
        if (items.Length > maxItems)
        {
            await _client.ReportSnapshotFailureAsync(
                link,
                command.Generation,
                "snapshot_too_large",
                $"The Jellyfin library contains {items.Length} supported items; limit is {maxItems}.",
                cancellationToken).ConfigureAwait(false);
            link.LastErrorCode = "snapshot_too_large";
            return;
        }

        var runId = await _client.StartSnapshotAsync(link, command.Generation, cancellationToken).ConfigureAwait(false);
        var pageSize = Math.Clamp(command.PageSize, 25, 1_000);
        var pageCount = 0;
        var rollingChecksum = string.Empty;
        for (var offset = 0; offset < items.Length; offset += pageSize)
        {
            var page = items.Skip(offset).Take(pageSize).ToArray();
            rollingChecksum = await _client.SendSnapshotPageAsync(link, runId, pageCount, page, cancellationToken).ConfigureAwait(false);
            pageCount++;
        }

        await _client.CompleteSnapshotAsync(link, runId, pageCount, items.Length, rollingChecksum, cancellationToken).ConfigureAwait(false);
        link.LastSyncAtUtc = DateTime.UtcNow;
        link.LastErrorCode = string.Empty;
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
            plugin.SaveConfiguration();
        }
    }
}

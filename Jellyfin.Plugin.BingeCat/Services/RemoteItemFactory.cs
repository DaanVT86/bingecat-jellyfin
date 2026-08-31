using Jellyfin.Plugin.BingeCat.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.BingeCat.Services;

/// <summary>Maps exact Jellyfin identifiers and aggregate user data to the public plugin contract.</summary>
public sealed class RemoteItemFactory
{
    public RemoteHistoryItem? Create(BaseItem item, UserItemData userData)
    {
        if (item is not Movie && item is not Episode)
        {
            return null;
        }

        if (item is Episode episodeWithoutCoordinates
            && (episodeWithoutCoordinates.ParentIndexNumber is null
                || episodeWithoutCoordinates.IndexNumber is null
                || episodeWithoutCoordinates.ParentIndexNumber < 0
                || episodeWithoutCoordinates.IndexNumber < 1))
        {
            return null;
        }

        var result = new RemoteHistoryItem
        {
            RemoteItemId = item.Id.ToString("D"),
            ItemType = item is Episode ? "episode" : "movie",
            Title = item.Name,
            Ids = ProviderIds(item.ProviderIds),
            Played = userData.Played,
            PlayCount = Math.Max(0, userData.PlayCount),
            LastPlayedAt = userData.LastPlayedDate,
            PlaybackPositionMs = Math.Max(0, userData.PlaybackPositionTicks / TimeSpan.TicksPerMillisecond),
            RuntimeMs = item.RunTimeTicks.HasValue
                ? Math.Max(0, item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond)
                : null
        };
        if (item is Episode episode)
        {
            result.SeasonNumber = episode.ParentIndexNumber;
            result.EpisodeNumber = episode.IndexNumber;
            result.RemoteSeriesId = episode.SeriesId.ToString("D");
            if (episode.Series is not null)
            {
                result.SeriesIds = ProviderIds(episode.Series.ProviderIds);
            }
        }

        return result;
    }

    private static Dictionary<string, object> ProviderIds(IReadOnlyDictionary<string, string>? ids)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (ids is null)
        {
            return result;
        }

        Add(ids, result, MetadataProvider.Imdb, "imdb", false);
        Add(ids, result, MetadataProvider.Tmdb, "tmdb", true);
        Add(ids, result, MetadataProvider.Tvdb, "tvdb", true);
        return result;
    }

    private static void Add(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, object> target,
        MetadataProvider provider,
        string key,
        bool numeric)
    {
        if (!source.TryGetValue(provider.ToString(), out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (numeric)
        {
            if (long.TryParse(value, out var parsed) && parsed > 0)
            {
                target[key] = parsed;
            }

            return;
        }

        target[key] = value;
    }
}

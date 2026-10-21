using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.BingeCat.Api;

public sealed class DeviceCodeRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("plugin_instance_uuid")]
    public string PluginInstanceUuid { get; init; } = string.Empty;

    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("server_name")]
    public string ServerName { get; init; } = string.Empty;

    [JsonPropertyName("server_version")]
    public string ServerVersion { get; init; } = string.Empty;

    [JsonPropertyName("remote_user_id")]
    public string RemoteUserId { get; init; } = string.Empty;

    [JsonPropertyName("remote_user_name")]
    public string RemoteUserName { get; init; } = string.Empty;

    [JsonPropertyName("plugin_version")]
    public string PluginVersion { get; init; } = PluginVersionInfo.Value;

    [JsonPropertyName("capabilities")]
    public Dictionary<string, bool> Capabilities { get; init; } = new()
    {
        ["history_state_read"] = true,
        ["resume_state_read"] = true,
        ["websocket_user_data"] = true
    };
}

public sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri_complete")]
    public string VerificationUriComplete { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 5;
}

public sealed class DeviceTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("connection_id")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("sync_generation")]
    public long SyncGeneration { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}

public sealed class HeartbeatResponse
{
    [JsonPropertyName("connection")]
    public HeartbeatConnection? Connection { get; set; }

    [JsonPropertyName("sync_command")]
    public SyncCommand? SyncCommand { get; set; }
}

public sealed class HeartbeatConnection
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("acknowledged_sync_generation")]
    public long AcknowledgedSyncGeneration { get; set; }
}

public sealed class SyncCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("generation")]
    public long Generation { get; set; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; } = 250;

    [JsonPropertyName("max_items")]
    public int MaxItems { get; set; } = 50_000;
}

public sealed class SnapshotStartResponse
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;
}

public sealed class SnapshotPageResponse
{
    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = string.Empty;
}

public sealed class EventBatchResponse
{
    [JsonPropertyName("accepted")]
    public List<string> Accepted { get; set; } = [];

    [JsonPropertyName("duplicates")]
    public List<string> Duplicates { get; set; } = [];

    [JsonPropertyName("rejected")]
    public Dictionary<string, string> Rejected { get; set; } = [];

    [JsonPropertyName("snapshot_required")]
    public bool SnapshotRequired { get; set; }
}

public sealed class RemoteHistoryItem
{
    [JsonPropertyName("remote_item_id")]
    public string RemoteItemId { get; set; } = string.Empty;

    [JsonPropertyName("item_type")]
    public string ItemType { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("remote_series_id")]
    public string? RemoteSeriesId { get; set; }

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episode_number")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("ids")]
    public Dictionary<string, object> Ids { get; set; } = [];

    [JsonPropertyName("series_ids")]
    public Dictionary<string, object> SeriesIds { get; set; } = [];

    [JsonPropertyName("played")]
    public bool Played { get; set; }

    [JsonPropertyName("play_count")]
    public int PlayCount { get; set; }

    [JsonPropertyName("last_played_at")]
    public DateTime? LastPlayedAt { get; set; }

    [JsonPropertyName("playback_position_ms")]
    public long PlaybackPositionMs { get; set; }

    [JsonPropertyName("runtime_ms")]
    public long? RuntimeMs { get; set; }
}

public sealed class HistoryEvent
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = Guid.NewGuid().ToString("D");

    [JsonPropertyName("sequence_no")]
    public long SequenceNo { get; set; }

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "history.updated";

    [JsonPropertyName("occurred_at")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("item")]
    public RemoteHistoryItem Item { get; set; } = new();
}

internal static class PluginVersionInfo
{
    public static readonly string Value = typeof(PluginVersionInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}

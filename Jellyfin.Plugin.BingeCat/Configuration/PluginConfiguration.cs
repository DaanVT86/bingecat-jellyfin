using MediaBrowser.Model.Plugins;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.BingeCat.Configuration;

/// <summary>Persisted plugin configuration and per-user BingeCat bindings.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        ApiBaseUrl = "https://bingecat.com";
        PluginInstanceUuid = Guid.NewGuid().ToString("D");
        ProfileLinks = [];
        ServiceEnabled = true;
    }

    public string ApiBaseUrl { get; set; }

    public string PluginInstanceUuid { get; set; }

    public bool ServiceEnabled { get; set; }

    public List<ProfileLink> ProfileLinks { get; set; }

    [XmlIgnore]
    public object ProfileLinksSyncRoot { get; } = new();

    public ProfileLink? FindLink(Guid userId)
    {
        lock (ProfileLinksSyncRoot)
        {
            return ProfileLinks.FirstOrDefault(link =>
                Guid.TryParse(link.JellyfinUserId, out var linkedId) && linkedId == userId);
        }
    }
}

/// <summary>One local Jellyfin user paired with one active BingeCat profile.</summary>
public sealed class ProfileLink
{
    public string JellyfinUserId { get; set; } = string.Empty;

    public string JellyfinUserName { get; set; } = string.Empty;

    public string DeviceToken { get; set; } = string.Empty;

    public string ConnectionId { get; set; } = string.Empty;

    public DateTime LinkedAtUtc { get; set; }

    public DateTime? LastHeartbeatAtUtc { get; set; }

    public DateTime? LastSyncAtUtc { get; set; }

    public string LastErrorCode { get; set; } = string.Empty;

    public ProfileLink Copy()
    {
        return new ProfileLink
        {
            JellyfinUserId = JellyfinUserId,
            JellyfinUserName = JellyfinUserName,
            DeviceToken = DeviceToken,
            ConnectionId = ConnectionId,
            LinkedAtUtc = LinkedAtUtc,
            LastHeartbeatAtUtc = LastHeartbeatAtUtc,
            LastSyncAtUtc = LastSyncAtUtc,
            LastErrorCode = LastErrorCode
        };
    }
}

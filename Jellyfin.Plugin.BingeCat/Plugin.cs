using System.Globalization;
using Jellyfin.Plugin.BingeCat.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BingeCat;

/// <summary>Jellyfin integration that pushes profile-scoped history to BingeCat.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override Guid Id => Guid.Parse("832bd9b3-75aa-4dd7-8477-8d13de65f9ee");

    public override string Name => "BingeCat Sync";

    public override string Description => "One-way watched-history sync from each paired Jellyfin user to a BingeCat profile.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}

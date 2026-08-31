using Jellyfin.Plugin.BingeCat.Api;
using Jellyfin.Plugin.BingeCat.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BingeCat;

/// <summary>Registers all plugin services in Jellyfin's application container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(HttpClientNames.BingeCat, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        serviceCollection.AddSingleton<BingeCatClient>();
        serviceCollection.AddSingleton<PairingService>();
        serviceCollection.AddSingleton<OutboxStore>();
        serviceCollection.AddSingleton<RemoteItemFactory>();
        serviceCollection.AddHostedService<SyncService>();
    }
}

internal static class HttpClientNames
{
    public const string BingeCat = "BingeCat";
}

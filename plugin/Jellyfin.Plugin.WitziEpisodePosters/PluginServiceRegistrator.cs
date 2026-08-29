using Jellyfin.Plugin.WitziEpisodePosters.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WitziEpisodePosters;

/// <summary>
/// Registers the plugin's services with Jellyfin's host.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Serves the theme, pre-paint layer, and browser helper with index.html at
        // request time, so a palette picked on the configuration page reaches the
        // browser on the next page load even where the web folder is not writable or
        // a jellyfin-web upgrade has replaced the file. Kill-switchable through the
        // configuration page, which falls back to the startup write on its own.
        serviceCollection.AddSingleton<IStartupFilter, WitziIndexInjectionStartupFilter>();
    }
}

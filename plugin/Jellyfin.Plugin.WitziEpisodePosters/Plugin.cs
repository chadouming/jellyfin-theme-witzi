using Jellyfin.Plugin.WitziEpisodePosters.Configuration;
using Jellyfin.Plugin.WitziEpisodePosters.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters;

/// <summary>
/// Generates persistent portrait artwork for TV episodes.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    /// <param name="xmlSerializer">The configuration serializer.</param>
    /// <param name="logger">The logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;
        ConfigurationChanged += OnConfigurationChanged;
    }

    /// <summary>
    /// Gets the loaded plugin instance, or null before Jellyfin has constructed it.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Witzi Episode Posters";

    /// <inheritdoc />
    public override string Description => "Builds Witzi-styled portrait episode posters and installs the Witzi theme and browser helper into Jellyfin Web.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("896d9ba3-a129-4fcc-be38-c0521ebe2d8f");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = "Jellyfin.Plugin.WitziEpisodePosters.Configuration.configPage.html"
        };
    }

    // The request-time middleware reads Configuration per request, so a palette
    // picked on the configuration page is already live on the next page load. This
    // only reconciles index.html on disk, which matters when request-time injection
    // is turned off, and clears the file when it is turned back on.
    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration witziConfiguration)
        {
            return;
        }

        var installer = new WitziWebInstaller(ApplicationPaths, _logger);

        // The configuration endpoint should not block on a file rewrite, and it
        // has nowhere to report one that fails. SyncAsync logs its own
        // failures, so this only has to keep an unexpected one from reaching the
        // task scheduler as an unobserved exception.
        _ = Task.Run(async () =>
        {
            try
            {
                await installer.SyncAsync(witziConfiguration, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not reconcile Jellyfin Web's index.html after a configuration change.");
            }
        });
    }
}

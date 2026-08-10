using Jellyfin.Plugin.WitziEpisodePosters.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WitziEpisodePosters;

/// <summary>
/// Generates persistent portrait artwork for TV episodes.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "Witzi Episode Posters";

    /// <inheritdoc />
    public override string Description => "Builds Witzi-styled portrait episode posters and installs the Witzi browser helper for Jellyfin Web.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("896d9ba3-a129-4fcc-be38-c0521ebe2d8f");
}

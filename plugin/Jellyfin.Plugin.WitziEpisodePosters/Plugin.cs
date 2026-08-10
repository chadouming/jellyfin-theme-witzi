using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.WitziEpisodePosters;

/// <summary>
/// Generates persistent portrait artwork for TV episodes.
/// </summary>
public sealed class Plugin : BasePlugin
{
    /// <inheritdoc />
    public override string Name => "Witzi Episode Posters";

    /// <inheritdoc />
    public override string Description => "Builds Witzi-styled portrait episode posters from representative video frames.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("896d9ba3-a129-4fcc-be38-c0521ebe2d8f");
}

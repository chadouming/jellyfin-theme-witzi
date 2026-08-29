using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WitziEpisodePosters.Configuration;

/// <summary>
/// Stores configuration for the Witzi episode poster plugin.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the compiled theme is written into Jellyfin Web's
    /// index.html.
    /// </summary>
    /// <remarks>
    /// Jellyfin renders the Custom CSS field from a React component that waits on the branding
    /// request, so a theme pasted there cannot reach the browser until after the first paint: the
    /// page shows stock Jellyfin colours and landscape home rows, then snaps to Witzi. Serving the
    /// bundle from index.html removes that. The bundle is written at the end of &lt;body&gt;, which is the
    /// only place it outranks the palette Jellyfin Web installs at runtime, so it also outranks the
    /// Custom CSS field. Turn this off to go back to Custom CSS delivery, or to run a hand-edited
    /// copy of the theme.
    /// </remarks>
    public bool InjectTheme { get; set; } = true;

    /// <summary>
    /// Gets or sets the palette served pre-paint. See <see cref="WitziPalettes.All"/>.
    /// </summary>
    public string Palette { get; set; } = WitziPalettes.Default;

    /// <summary>
    /// Gets or sets a value indicating whether the request-time injection of the Witzi web assets
    /// into Jellyfin Web's index.html is turned off.
    /// </summary>
    /// <remarks>
    /// Left off, the pre-paint layer, theme, and browser helper are added to the index.html
    /// response as it is served, which needs no write access to the web folder and survives a
    /// jellyfin-web upgrade replacing the file. Turn this on to rely on the startup write into
    /// index.html alone — for a server behind something that serves the web folder itself, or to
    /// run a hand-edited copy of the file.
    /// </remarks>
    public bool DisableIndexMiddleware { get; set; }
}

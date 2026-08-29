namespace Jellyfin.Plugin.WitziEpisodePosters.Configuration;

/// <summary>
/// The compiled theme bundles the plugin can serve to Jellyfin Web.
/// </summary>
public static class WitziPalettes
{
    /// <summary>
    /// The palette used when configuration names one that this build does not carry.
    /// </summary>
    public const string Default = "mocha";

    /// <summary>
    /// Gets every palette name this build embeds, in the order the configuration page lists them.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "mocha",
        "latte",
        "nord",
        "solarized",
        "dracula",
        "gruvbox"
    ];

    /// <summary>
    /// Resolves a configured palette name to one this build actually embeds.
    /// </summary>
    /// <param name="palette">The configured palette name.</param>
    /// <returns>A palette name that has an embedded bundle.</returns>
    public static string Normalize(string? palette)
    {
        if (string.IsNullOrWhiteSpace(palette))
        {
            return Default;
        }

        var trimmed = palette.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return Default;
    }

    /// <summary>
    /// Builds the embedded resource name for a palette.
    /// </summary>
    /// <param name="palette">A palette name from <see cref="All"/>.</param>
    /// <returns>The manifest resource name of that palette's compiled bundle.</returns>
    public static string ResourceName(string palette)
        => $"Jellyfin.Plugin.WitziEpisodePosters.Web.Themes.witzi-{palette}.css";
}

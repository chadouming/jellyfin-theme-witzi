using Jellyfin.Plugin.WitziEpisodePosters.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.Services;

/// <summary>
/// The Witzi payload that belongs in Jellyfin Web's index.html: the pre-paint layer, the compiled
/// theme for the configured palette, and the browser helper.
/// </summary>
/// <remarks>
/// Every piece is an embedded resource, so a set is immutable once built and only the configured
/// palette can change it. Instances are therefore cached per palette and shared by both delivery
/// paths — the startup write into index.html and the request-time middleware — which also keeps
/// the two byte-identical, so neither one sees the other's work as a change to undo.
/// </remarks>
public sealed class WitziWebAssets
{
    private const string HelperResourceName = "Jellyfin.Plugin.WitziEpisodePosters.Web.witzi-posters.js";
    private const string CriticalResourceName = "Jellyfin.Plugin.WitziEpisodePosters.Web.witzi-critical.css";

    private static readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static readonly Dictionary<string, WitziWebAssets> _cache = new(StringComparer.Ordinal);

    private WitziWebAssets(string helper, string critical, string? theme)
    {
        Helper = helper;
        Critical = critical;
        Theme = theme;
    }

    /// <summary>
    /// Gets the browser helper JavaScript.
    /// </summary>
    public string Helper { get; }

    /// <summary>
    /// Gets the pre-paint stylesheet.
    /// </summary>
    public string Critical { get; }

    /// <summary>
    /// Gets the compiled theme, or null when the configuration turns theme delivery off.
    /// </summary>
    public string? Theme { get; }

    /// <summary>
    /// Builds the payload a configuration asks for, reusing a cached copy when one exists.
    /// </summary>
    /// <param name="configuration">The configuration to build a payload for.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The payload, or null when a resource this build should carry is missing or unusable.</returns>
    public static async Task<WitziWebAssets?> LoadAsync(
        PluginConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var palette = configuration.InjectTheme
            ? WitziPalettes.Normalize(configuration.Palette)
            : null;

        if (palette is not null && !string.Equals(palette, configuration.Palette, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "This build has no {Configured} palette, so Jellyfin Web is served {Palette} instead.",
                configuration.Palette,
                palette);
        }

        var key = palette ?? string.Empty;

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var helper = await ReadEmbeddedResource(HelperResourceName, cancellationToken).ConfigureAwait(false);
            if (helper is null)
            {
                logger.LogError("Cannot serve the Witzi browser helper because embedded resource {ResourceName} is missing.", HelperResourceName);
                return null;
            }

            // An inline <script> ends at the first closing tag in its source, so one
            // in the helper would cut it short and spill the remainder into the page.
            if (helper.Contains("</script", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Cannot serve the Witzi browser helper because its source contains a closing script tag.");
                return null;
            }

            var critical = await ReadEmbeddedResource(CriticalResourceName, cancellationToken).ConfigureAwait(false);
            if (critical is null)
            {
                logger.LogError("Cannot serve the Witzi pre-paint layer because embedded resource {ResourceName} is missing.", CriticalResourceName);
                return null;
            }

            if (critical.Contains("</style", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Cannot serve the Witzi pre-paint layer because its source contains a closing style tag.");
                return null;
            }

            string? theme = null;
            if (palette is not null)
            {
                var resourceName = WitziPalettes.ResourceName(palette);
                theme = await ReadEmbeddedResource(resourceName, cancellationToken).ConfigureAwait(false);
                if (theme is null)
                {
                    logger.LogError("Cannot serve the Witzi theme because embedded resource {ResourceName} is missing.", resourceName);
                    return null;
                }

                if (theme.Contains("</style", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("Cannot serve the Witzi theme because the {Palette} bundle contains a closing style tag.", palette);
                    return null;
                }
            }

            var assets = new WitziWebAssets(helper, critical, theme);
            _cache[key] = assets;
            return assets;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static async Task<string?> ReadEmbeddedResource(string resourceName, CancellationToken cancellationToken)
    {
        await using var resource = typeof(WitziWebAssets).Assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            return null;
        }

        using var reader = new StreamReader(resource);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd();
    }
}

using System.Text.RegularExpressions;
using Jellyfin.Plugin.WitziEpisodePosters.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.Services;

/// <summary>
/// Writes the Witzi pre-paint layer, compiled theme, and browser helper into Jellyfin Web.
/// </summary>
public sealed class WitziWebInstaller
{
    private const string HelperResourceName = "Jellyfin.Plugin.WitziEpisodePosters.Web.witzi-posters.js";
    private const string CriticalResourceName = "Jellyfin.Plugin.WitziEpisodePosters.Web.witzi-critical.css";
    private const string HelperStartMarker = "<!-- BEGIN Witzi Theme Browser Helper -->";
    private const string HelperEndMarker = "<!-- END Witzi Theme Browser Helper -->";
    private const string CriticalStartMarker = "<!-- BEGIN Witzi Theme Pre-Paint Layer -->";
    private const string CriticalEndMarker = "<!-- END Witzi Theme Pre-Paint Layer -->";
    private const string ThemeStartMarker = "<!-- BEGIN Witzi Theme Stylesheet -->";
    private const string ThemeEndMarker = "<!-- END Witzi Theme Stylesheet -->";
    private static readonly Regex HelperBlockPattern = BlockPattern(HelperStartMarker, HelperEndMarker);
    private static readonly Regex CriticalBlockPattern = BlockPattern(CriticalStartMarker, CriticalEndMarker);
    private static readonly Regex ThemeBlockPattern = BlockPattern(ThemeStartMarker, ThemeEndMarker);
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WitziWebInstaller"/> class.
    /// </summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    /// <param name="logger">The logger.</param>
    public WitziWebInstaller(IApplicationPaths applicationPaths, ILogger logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Brings Jellyfin Web's index.html in line with the current plugin configuration.
    /// </summary>
    /// <param name="configuration">The configuration to bring Jellyfin Web in line with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once index.html matches the configuration.</returns>
    public async Task InstallAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(_applicationPaths.WebPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("Cannot install the Witzi browser helper because Jellyfin Web index was not found at {Path}.", indexPath);
                return;
            }

            var helper = await ReadEmbeddedResource(HelperResourceName, cancellationToken).ConfigureAwait(false);
            if (helper is null)
            {
                _logger.LogError("Cannot install the Witzi browser helper because embedded resource {ResourceName} is missing.", HelperResourceName);
                return;
            }

            if (helper.Contains("</script", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Cannot install the Witzi browser helper because its source contains a closing script tag.");
                return;
            }

            var critical = await ReadEmbeddedResource(CriticalResourceName, cancellationToken).ConfigureAwait(false);
            if (critical is null)
            {
                _logger.LogError("Cannot install the Witzi pre-paint layer because embedded resource {ResourceName} is missing.", CriticalResourceName);
                return;
            }

            if (critical.Contains("</style", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Cannot install the Witzi pre-paint layer because its source contains a closing style tag.");
                return;
            }

            var theme = await ReadConfiguredTheme(configuration, cancellationToken).ConfigureAwait(false);

            var document = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
            var criticalInjection = StyleBlock(CriticalStartMarker, CriticalEndMarker, critical);
            var helperInjection = $"{HelperStartMarker}{Environment.NewLine}<script>{Environment.NewLine}{helper}{Environment.NewLine}</script>{Environment.NewLine}{HelperEndMarker}";

            // The pre-paint rules only do their job if the browser has them
            // before Jellyfin renders, so they belong in head rather than beside
            // the helper. User Custom CSS arrives long after a detail page has
            // already assembled itself in front of the viewer.
            var changed = TryApplyBlock(ref document, CriticalBlockPattern, criticalInjection, "</head>", indexPath);

            changed |= TryApplyBlock(ref document, HelperBlockPattern, helperInjection, "</body>", indexPath);

            // The theme goes at the end of body rather than in head. Jellyfin
            // Web installs its own palette after anything head already carries:
            // MUI writes a :root block of --jf-palette-* values into head as
            // the bundle boots, and themes/<id>/theme.css arrives as a <link>
            // React renders inside #reactRoot. The theme's :root bridge ties
            // with both on specificity, so from head it loses every tie and the
            // page keeps looking untouched. Nothing Jellyfin renders comes after
            // it here. Jellyfin Web loads its bundles with defer, so the parser
            // still reaches this block before the app boots and the theme is in
            // place for the first paint either way.
            //
            // It anchors to the helper's opening marker rather than </body>.
            // The helper is an inline script that runs the moment the parser
            // reaches it, and its first act is to look for the theme's
            // --witzi-theme-active, so the theme has to be parsed first. A
            // </body> anchor gets that wrong on an upgrade, where the helper
            // block is already in place and only the theme is being inserted.
            var themeAnchor = document.Contains(HelperStartMarker, StringComparison.Ordinal)
                ? HelperStartMarker
                : "</body>";

            // Releases through 1.1.24 wrote the theme into head, and
            // TryApplyBlock updates a block where it already lives, so one left
            // there is dropped first and reinstalled at the new anchor.
            changed |= TryEvictThemeFromHead(ref document);
            changed |= theme is null
                ? TryRemoveBlock(ref document, ThemeBlockPattern)
                : TryApplyBlock(ref document, ThemeBlockPattern, StyleBlock(ThemeStartMarker, ThemeEndMarker, theme), themeAnchor, indexPath);

            if (!changed)
            {
                _logger.LogDebug("Jellyfin Web already carries the current Witzi pre-paint layer, theme, and browser helper.");
                return;
            }

            await WriteIndex(indexPath, document, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Installed the Witzi web assets into {Path}.", indexPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Jellyfin Web is not writable. Grant the Jellyfin service write access to index.html or install the helper through a compatible JavaScript injector.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not update Jellyfin Web index with the Witzi web assets.");
        }
    }

    private static Regex BlockPattern(string startMarker, string endMarker) => new(
        $"{Regex.Escape(startMarker)}[\\s\\S]*?{Regex.Escape(endMarker)}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string StyleBlock(string startMarker, string endMarker, string css)
        => $"{startMarker}{Environment.NewLine}<style>{Environment.NewLine}{css}{Environment.NewLine}</style>{Environment.NewLine}{endMarker}";

    private static async Task<string?> ReadEmbeddedResource(string resourceName, CancellationToken cancellationToken)
    {
        await using var resource = typeof(WitziWebInstaller).Assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            return null;
        }

        using var reader = new StreamReader(resource);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd();
    }

    private async Task<string?> ReadConfiguredTheme(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.InjectTheme)
        {
            return null;
        }

        var palette = WitziPalettes.Normalize(configuration.Palette);
        if (!string.Equals(palette, configuration.Palette, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "This build has no {Configured} palette, so Jellyfin Web is served {Palette} instead.",
                configuration.Palette,
                palette);
        }

        var resourceName = WitziPalettes.ResourceName(palette);
        var theme = await ReadEmbeddedResource(resourceName, cancellationToken).ConfigureAwait(false);
        if (theme is null)
        {
            _logger.LogError("Cannot install the Witzi theme because embedded resource {ResourceName} is missing.", resourceName);
            return null;
        }

        if (theme.Contains("</style", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Cannot install the Witzi theme because the {Palette} bundle contains a closing style tag.", palette);
            return null;
        }

        return theme;
    }

    // Drops a theme block an earlier release left in <head> so InstallAsync can
    // reinstall it before </body>. A block already past </head> is left alone:
    // TryApplyBlock updates it where it sits, which is what keeps injections
    // owned by other plugins in their original order.
    private bool TryEvictThemeFromHead(ref string document)
    {
        var headEnd = document.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
        {
            return false;
        }

        var existing = ThemeBlockPattern.Match(document);
        if (!existing.Success || existing.Index > headEnd)
        {
            return false;
        }

        return TryRemoveBlock(ref document, ThemeBlockPattern);
    }

    private bool TryRemoveBlock(ref string document, Regex blockPattern)
    {
        if (!blockPattern.IsMatch(document))
        {
            return false;
        }

        // The trailing newline belongs to the injection, so it goes with it.
        // Left behind, every disable and re-enable cycle would add a blank line.
        document = Regex.Replace(
            blockPattern.Replace(document, string.Empty),
            @"\r?\n\r?\n(\r?\n)+",
            Environment.NewLine + Environment.NewLine);
        return true;
    }

    private bool TryApplyBlock(
        ref string document,
        Regex blockPattern,
        string injection,
        string anchorTag,
        string indexPath)
    {
        var existingBlocks = blockPattern.Matches(document);
        if (existingBlocks.Count == 1
            && string.Equals(existingBlocks[0].Value, injection, StringComparison.Ordinal))
        {
            return false;
        }

        if (existingBlocks.Count > 0)
        {
            // Replace the first block where it already lives and remove
            // accidental duplicates. Re-appending it before the anchor can
            // reorder or overwrite injections owned by other plugins.
            var replacementWritten = false;
            document = blockPattern.Replace(
                document,
                _ =>
                {
                    if (replacementWritten)
                    {
                        return string.Empty;
                    }

                    replacementWritten = true;
                    return injection;
                });

            return true;
        }

        var anchor = document.LastIndexOf(anchorTag, StringComparison.OrdinalIgnoreCase);
        if (anchor < 0)
        {
            _logger.LogWarning("Cannot install a Witzi block because {Path} has no {AnchorTag} tag.", indexPath, anchorTag);
            return false;
        }

        document = document.Insert(anchor, injection + Environment.NewLine);
        return true;
    }

    // Jellyfin Web cannot start without index.html, so it is replaced through a
    // staged file and a rename where that is possible: a crash, container stop,
    // or full disk partway through a direct write would otherwise leave the web
    // client truncated with no copy of the original left to restore.
    //
    // Staging needs write access to the directory, while rewriting the file in
    // place only needs write access to index.html itself. Container images
    // regularly grant the second and not the first, so a refused staging file
    // falls back rather than leaving the helper uninstalled.
    private async Task WriteIndex(
        string indexPath,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(indexPath)
            ?? throw new ArgumentException("The Jellyfin Web index path has no containing directory.", nameof(indexPath));
        var temporaryPath = Path.Combine(
            directory,
            "index.html.witzi-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, indexPath, true);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Only a permission refusal falls back. A full disk or I/O failure
            // surfaces as IOException, and rewriting in place after one of those
            // is how index.html gets truncated, so those keep propagating.
            _logger.LogWarning(
                ex,
                "Could not stage a replacement beside {Path}, so it will be rewritten in place. An interrupted write cannot be rolled back; grant the Jellyfin service write access to that directory to restore the safe path.",
                indexPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The staged copy is inert. Leaving it behind is better than
                // masking the original failure with a cleanup error.
            }
        }

        await File.WriteAllTextAsync(indexPath, content, cancellationToken).ConfigureAwait(false);
    }
}

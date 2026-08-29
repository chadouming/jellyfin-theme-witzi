using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.Services;

/// <summary>
/// Applies the Witzi blocks to a copy of Jellyfin Web's index.html.
/// </summary>
/// <remarks>
/// Both delivery paths run this: the startup task, which writes the result back to disk, and the
/// request-time middleware, which writes it to the response. Sharing one implementation is what
/// lets the middleware recognise a document the startup task already prepared and pass it through
/// untouched, and what lets it correct a stale block — an old palette left behind by a web folder
/// the plugin can no longer write to — instead of honouring it.
/// </remarks>
public static class WitziIndexDocument
{
    /// <summary>
    /// The opening marker of the browser helper block.
    /// </summary>
    public const string HelperStartMarker = "<!-- BEGIN Witzi Theme Browser Helper -->";

    private const string HelperEndMarker = "<!-- END Witzi Theme Browser Helper -->";
    private const string CriticalStartMarker = "<!-- BEGIN Witzi Theme Pre-Paint Layer -->";
    private const string CriticalEndMarker = "<!-- END Witzi Theme Pre-Paint Layer -->";
    private const string ThemeStartMarker = "<!-- BEGIN Witzi Theme Stylesheet -->";
    private const string ThemeEndMarker = "<!-- END Witzi Theme Stylesheet -->";

    private static readonly Regex _helperBlockPattern = BlockPattern(HelperStartMarker, HelperEndMarker);
    private static readonly Regex _criticalBlockPattern = BlockPattern(CriticalStartMarker, CriticalEndMarker);
    private static readonly Regex _themeBlockPattern = BlockPattern(ThemeStartMarker, ThemeEndMarker);

    /// <summary>
    /// Brings a document in line with a payload.
    /// </summary>
    /// <param name="document">The document, replaced with the updated one when this returns true.</param>
    /// <param name="assets">The payload the document should carry.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="target">A description of the document, used when reporting a missing anchor tag.</param>
    /// <returns>True when the document changed.</returns>
    public static bool TryApply(ref string document, WitziWebAssets assets, ILogger logger, string target)
    {
        var criticalInjection = StyleBlock(CriticalStartMarker, CriticalEndMarker, assets.Critical);
        var helperInjection = $"{HelperStartMarker}{Environment.NewLine}<script>{Environment.NewLine}{assets.Helper}{Environment.NewLine}</script>{Environment.NewLine}{HelperEndMarker}";

        // The pre-paint rules only do their job if the browser has them before
        // Jellyfin renders, so they belong in head rather than beside the helper.
        // User Custom CSS arrives long after a detail page has already assembled
        // itself in front of the viewer.
        var changed = TryApplyBlock(ref document, _criticalBlockPattern, criticalInjection, "</head>", logger, target);

        changed |= TryApplyBlock(ref document, _helperBlockPattern, helperInjection, "</body>", logger, target);

        // The theme goes at the end of body rather than in head. Jellyfin Web
        // installs its own palette after anything head already carries: MUI writes
        // a :root block of --jf-palette-* values into head as the bundle boots, and
        // themes/<id>/theme.css arrives as a <link> React renders inside #reactRoot.
        // The theme's :root bridge ties with both on specificity, so from head it
        // loses every tie and the page keeps looking untouched. Nothing Jellyfin
        // renders comes after it here. Jellyfin Web loads its bundles with defer, so
        // the parser still reaches this block before the app boots and the theme is
        // in place for the first paint either way.
        //
        // It anchors to the helper's opening marker rather than </body>. The helper
        // is an inline script that runs the moment the parser reaches it, and its
        // first act is to look for the theme's --witzi-theme-active, so the theme has
        // to be parsed first. A </body> anchor gets that wrong on an upgrade, where
        // the helper block is already in place and only the theme is being inserted.
        var themeAnchor = document.Contains(HelperStartMarker, StringComparison.Ordinal)
            ? HelperStartMarker
            : "</body>";

        // Releases through 1.1.25 wrote the theme into head, and TryApplyBlock updates
        // a block where it already lives, so one left there is dropped first and
        // reinstalled at the new anchor.
        changed |= TryEvictThemeFromHead(ref document);
        changed |= assets.Theme is null
            ? TryRemoveBlock(ref document, _themeBlockPattern)
            : TryApplyBlock(ref document, _themeBlockPattern, StyleBlock(ThemeStartMarker, ThemeEndMarker, assets.Theme), themeAnchor, logger, target);

        return changed;
    }

    /// <summary>
    /// Strips every Witzi block from a document.
    /// </summary>
    /// <remarks>
    /// Used to clear the on-disk index.html once the request-time middleware owns delivery, so a
    /// block an earlier release wrote there cannot outlive the release that put it there. A web
    /// folder too locked down to clear needs no follow-up: the middleware rewrites a stale block in
    /// the response it serves.
    /// </remarks>
    /// <param name="document">The document, replaced with the stripped one when this returns true.</param>
    /// <returns>True when the document changed.</returns>
    public static bool TryRemoveAll(ref string document)
    {
        var changed = TryRemoveBlock(ref document, _criticalBlockPattern);
        changed |= TryRemoveBlock(ref document, _themeBlockPattern);
        changed |= TryRemoveBlock(ref document, _helperBlockPattern);
        return changed;
    }

    private static Regex BlockPattern(string startMarker, string endMarker) => new(
        $"{Regex.Escape(startMarker)}[\\s\\S]*?{Regex.Escape(endMarker)}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string StyleBlock(string startMarker, string endMarker, string css)
        => $"{startMarker}{Environment.NewLine}<style>{Environment.NewLine}{css}{Environment.NewLine}</style>{Environment.NewLine}{endMarker}";

    // Drops a theme block an earlier release left in <head> so TryApply can reinstall
    // it before </body>. A block already past </head> is left alone: TryApplyBlock
    // updates it where it sits, which is what keeps injections owned by other plugins
    // in their original order.
    private static bool TryEvictThemeFromHead(ref string document)
    {
        var headEnd = document.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
        {
            return false;
        }

        var existing = _themeBlockPattern.Match(document);
        if (!existing.Success || existing.Index > headEnd)
        {
            return false;
        }

        return TryRemoveBlock(ref document, _themeBlockPattern);
    }

    private static bool TryRemoveBlock(ref string document, Regex blockPattern)
    {
        if (!blockPattern.IsMatch(document))
        {
            return false;
        }

        // The trailing newline belongs to the injection, so it goes with it. Left
        // behind, every disable and re-enable cycle would add a blank line.
        document = Regex.Replace(
            blockPattern.Replace(document, string.Empty),
            @"\r?\n\r?\n(\r?\n)+",
            Environment.NewLine + Environment.NewLine);
        return true;
    }

    private static bool TryApplyBlock(
        ref string document,
        Regex blockPattern,
        string injection,
        string anchorTag,
        ILogger logger,
        string target)
    {
        var existingBlocks = blockPattern.Matches(document);
        if (existingBlocks.Count == 1
            && string.Equals(existingBlocks[0].Value, injection, StringComparison.Ordinal))
        {
            return false;
        }

        if (existingBlocks.Count > 0)
        {
            // Replace the first block where it already lives and remove accidental
            // duplicates. Re-appending it before the anchor can reorder or overwrite
            // injections owned by other plugins.
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
            logger.LogWarning("Cannot install a Witzi block because {Target} has no {AnchorTag} tag.", target, anchorTag);
            return false;
        }

        document = document.Insert(anchor, injection + Environment.NewLine);
        return true;
    }
}

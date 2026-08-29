using Jellyfin.Plugin.WitziEpisodePosters.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.Services;

/// <summary>
/// Writes the Witzi pre-paint layer, compiled theme, and browser helper into Jellyfin Web's
/// index.html on disk.
/// </summary>
/// <remarks>
/// This is the fallback delivery path, used only while <see cref="PluginConfiguration.DisableIndexMiddleware"/>
/// is set. <see cref="WitziIndexInjectionStartupFilter"/> otherwise adds the same blocks to the
/// index.html response as it is served, which is what makes the theme work on a web folder the
/// Jellyfin service account cannot write and across a jellyfin-web upgrade that replaces the file.
/// Writing to disk stays available for a deployment where something other than Jellyfin serves the
/// web folder, and while the middleware is on this class instead clears what earlier releases wrote
/// there, so exactly one path owns delivery.
/// </remarks>
public sealed class WitziWebInstaller
{
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
    /// Brings Jellyfin Web's index.html on disk in line with the current plugin configuration:
    /// carrying the Witzi blocks while the request-time middleware is turned off, and carrying
    /// none while it is on.
    /// </summary>
    /// <param name="configuration">The configuration to bring Jellyfin Web in line with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once index.html matches the configuration.</returns>
    public async Task SyncAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(_applicationPaths.WebPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("Cannot write the Witzi web assets because Jellyfin Web index was not found at {Path}.", indexPath);
                return;
            }

            var document = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);

            if (!configuration.DisableIndexMiddleware)
            {
                // The middleware owns delivery, so the file should carry nothing. This
                // clears blocks written by 1.1.24 and 1.1.25, whose palette would
                // otherwise stay in the file after the configuration moved on.
                if (!WitziIndexDocument.TryRemoveAll(ref document))
                {
                    _logger.LogDebug("The Witzi web assets are served at request time and Jellyfin Web's index.html carries none of its own.");
                    return;
                }

                await WriteIndex(indexPath, document, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed the Witzi web assets from {Path}; they are served at request time instead.", indexPath);
                return;
            }

            var assets = await WitziWebAssets
                .LoadAsync(configuration, _logger, cancellationToken)
                .ConfigureAwait(false);
            if (assets is null)
            {
                return;
            }

            if (!WitziIndexDocument.TryApply(ref document, assets, _logger, indexPath))
            {
                _logger.LogDebug("Jellyfin Web already carries the current Witzi pre-paint layer, theme, and browser helper.");
                return;
            }

            await WriteIndex(indexPath, document, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Installed the Witzi web assets into {Path}.", indexPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Not fatal while the middleware is on: it serves the same blocks without
            // touching the file, and corrects a stale one it cannot delete. Only a
            // server that has turned the middleware off needs this write to succeed.
            _logger.LogWarning(ex, "Jellyfin Web's index.html is not writable, so it was left as it is.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not update Jellyfin Web's index.html, so it was left as it is.");
        }
    }

    // Jellyfin Web cannot start without index.html, so it is replaced through a
    // staged file and a rename where that is possible: a crash, container stop,
    // or full disk partway through a direct write would otherwise leave the web
    // client truncated with no copy of the original left to restore.
    //
    // Staging needs write access to the directory, while rewriting the file in
    // place only needs write access to index.html itself. Container images
    // regularly grant the second and not the first, so a refused staging file
    // falls back rather than leaving the assets uninstalled.
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

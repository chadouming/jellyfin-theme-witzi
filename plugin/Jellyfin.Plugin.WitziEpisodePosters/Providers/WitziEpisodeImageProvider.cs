using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.WitziEpisodePosters.Providers;

/// <summary>
/// Supplies the dedicated Witzi poster as an episode's Primary image on every
/// metadata refresh.
/// </summary>
/// <remarks>
/// Installing the poster under Jellyfin's recognized sidecar name is enough to
/// select it once, but a library scan re-runs the image providers and rebuilds
/// the choice from whatever they return. Contributing the poster as a local
/// image makes it part of that decision every time instead of something a later
/// refresh can quietly replace.
///
/// Two details of Jellyfin's merge matter here. ItemImageProvider keeps the
/// first local image offered for a type, and providers are ordered by the
/// library's configured image fetcher order and then by <see cref="Order"/>, so
/// sorting ahead of Jellyfin's own local provider wins the merge on a library
/// that has not pinned an explicit order. Separately, a winning image stored
/// beside the media marks Primary as locally provided, which suppresses the
/// remote fetch that would otherwise download an episode screenshot over it.
/// </remarks>
public sealed class WitziEpisodeImageProvider : ILocalImageProvider, IHasOrder
{
    /// <inheritdoc />
    public string Name => "Witzi Episode Posters";

    /// <summary>
    /// Gets the provider order. Jellyfin's own episode local image provider uses
    /// zero, so a lower value offers the Witzi poster first.
    /// </summary>
    public int Order => -1;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
    {
        return item is Episode;
    }

    /// <inheritdoc />
    public IEnumerable<LocalImageInfo> GetImages(BaseItem item, IDirectoryService directoryService)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(directoryService);

        var poster = ResolvePrimary(item.Path, directoryService);
        if (poster is null)
        {
            yield break;
        }

        // Only one Primary is useful.
        yield return new LocalImageInfo
        {
            FileInfo = poster,
            Type = ImageType.Primary
        };
    }

    /// <summary>
    /// Picks which file to offer as Primary.
    /// </summary>
    /// <remarks>
    /// GenerateEpisodePostersTask installs the poster under the sidecar name from
    /// <see cref="WitziPosterFiles.GetInstalledPosterPath"/> and registers that copy. Offering
    /// the "-witzi.jpg" source instead would make this provider win the scan's image merge with
    /// a different path than the task recorded, so ItemImageProvider would rewrite Primary on
    /// every scan and the task would rewrite it back afterwards. Each pass saved the episode
    /// and notified every change consumer, without anything about the artwork actually
    /// changing. Preferring the installed copy keeps both halves on the same path.
    ///
    /// The source is still the fallback, so an episode whose poster has not been installed yet
    /// keeps its artwork until the task runs. Both live beside the media, so either way Primary
    /// counts as locally provided and a remote fetcher will not replace it.
    /// </remarks>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <param name="directoryService">Directory service used to stat candidates.</param>
    /// <returns>The file to offer, or <c>null</c> when no poster exists.</returns>
    private static FileSystemMetadata? ResolvePrimary(string? mediaPath, IDirectoryService directoryService)
    {
        FileSystemMetadata? source = null;
        foreach (var posterPath in WitziPosterFiles.GetPosterPaths(mediaPath))
        {
            var candidate = directoryService.GetFile(posterPath);
            if (candidate is not null && !candidate.IsDirectory)
            {
                source = candidate;
                break;
            }
        }

        if (source is null)
        {
            return null;
        }

        var installedPath = WitziPosterFiles.GetInstalledPosterPath(mediaPath);
        if (installedPath is null
            || string.Equals(installedPath, source.FullName, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        // Length alone distinguishes the installed copy from unrelated artwork that happens to
        // occupy the sidecar name, and costs nothing: DirectoryService has already stat'ed the
        // directory. A byte comparison here would read two files per episode on every scan.
        var installed = directoryService.GetFile(installedPath);
        return installed is not null && !installed.IsDirectory && installed.Length == source.Length
            ? installed
            : source;
    }
}

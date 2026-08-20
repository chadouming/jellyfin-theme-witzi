using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

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

        foreach (var posterPath in WitziPosterFiles.GetPosterPaths(item.Path))
        {
            var file = directoryService.GetFile(posterPath);
            if (file is null || file.IsDirectory)
            {
                continue;
            }

            // Only one Primary is useful, and the first candidate is the copy
            // stored with the media, which is the one that earns the protection
            // against a remote fetcher replacing it.
            yield return new LocalImageInfo
            {
                FileInfo = file,
                Type = ImageType.Primary
            };

            yield break;
        }
    }
}

using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.WitziEpisodePosters.Posters;

/// <summary>
/// Answers the two questions the tasks ask before doing any work: does this
/// episode already have a Witzi poster, and is that poster still the Primary
/// image Jellyfin will serve.
/// </summary>
internal sealed class PosterInspector
{
    private readonly IImageProcessor _imageProcessor;

    internal PosterInspector(IImageProcessor imageProcessor)
    {
        _imageProcessor = imageProcessor;
    }

    /// <summary>
    /// Finds the Witzi poster belonging to an episode.
    /// </summary>
    /// <param name="episode">Episode to inspect.</param>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <returns>The poster path, or <c>null</c> when the episode has none.</returns>
    internal string? FindExistingWitziPoster(Episode episode, string mediaPath)
    {
        var candidates = WitziPosterFiles.GetPosterPaths(mediaPath);
        var primary = episode.GetImageInfo(ImageType.Primary, 0);
        if (primary is not null
            && candidates.Any(candidate => PosterPaths.PathsEqual(primary.Path, candidate))
            && File.Exists(primary.Path)
            && HasExpectedDimensions(primary.Path))
        {
            return primary.Path;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && HasExpectedDimensions(candidate))
            {
                return candidate;
            }
        }

        return FindLegacyWitziPoster(episode, mediaPath);
    }

    /// <summary>
    /// Determines whether the episode's Primary image is the installed Witzi poster and will
    /// survive the next library scan, meaning there is nothing to do for this episode.
    /// </summary>
    /// <param name="episode">Episode to inspect.</param>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <param name="witziPosterPath">The episode's Witzi poster.</param>
    /// <returns><c>true</c> when the poster is already registered and unchallenged.</returns>
    internal bool IsPersistentWitziPrimary(Episode episode, string mediaPath, string witziPosterPath)
    {
        var primaryPosterPath = PosterSidecars.GetPrimaryPosterPath(mediaPath);
        var primary = episode.GetImageInfo(ImageType.Primary, 0);
        return primary is not null
            && PosterPaths.PathsEqual(primary.Path, primaryPosterPath)
            && (PosterPaths.PathsEqual(witziPosterPath, primaryPosterPath)
                || IsUnmodifiedSinceRegistration(primaryPosterPath, witziPosterPath, primary)
                || PosterPaths.FilesEqual(witziPosterPath, primaryPosterPath))
            && PosterSidecars.GetProviderPrimarySidecars(mediaPath)
                .All(path => PosterPaths.PathsEqual(path, primaryPosterPath));
    }

    /// <summary>
    /// Determines whether an image is one of this plugin's posters by its exact output size.
    /// </summary>
    /// <param name="image">Registered image metadata, used when it carries dimensions.</param>
    /// <param name="path">Path to measure when it does not.</param>
    /// <returns><c>true</c> when the image has the Witzi poster dimensions.</returns>
    internal bool HasExpectedDimensions(ItemImageInfo image, string path)
    {
        if (image.Width > 0 && image.Height > 0)
        {
            return image.Width == WitziPosterFiles.PosterWidth
                && image.Height == WitziPosterFiles.PosterHeight;
        }

        return HasExpectedDimensions(path);
    }

    private string? FindLegacyWitziPoster(Episode episode, string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var legacyPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath) + ".jpg");
        var primary = episode.GetImageInfo(ImageType.Primary, 0);

        // Releases through 0.1.10 used the generic <video basename>.jpg
        // sidecar. Exact output dimensions plus the registered Primary path
        // provide the only durable signature those versions left behind.
        return primary is not null
            && PosterPaths.PathsEqual(primary.Path, legacyPath)
            && File.Exists(legacyPath)
            && HasExpectedDimensions(primary, legacyPath)
                ? legacyPath
                : null;
    }

    // RegisterPoster records the installed file's write time, so an untouched
    // timestamp and size mean the Primary is still the copy this task made.
    // Confirming that costs two metadata reads instead of reading both posters
    // in full, which every already-current episode previously paid on each run.
    // Anything that did change falls through to the byte comparison.
    private static bool IsUnmodifiedSinceRegistration(
        string primaryPosterPath,
        string witziPosterPath,
        ItemImageInfo primary)
    {
        if (primary.DateModified == default)
        {
            return false;
        }

        try
        {
            var primaryInfo = new FileInfo(primaryPosterPath);
            var witziInfo = new FileInfo(witziPosterPath);
            return primaryInfo.Exists
                && witziInfo.Exists
                && primaryInfo.LastWriteTimeUtc == primary.DateModified
                && primaryInfo.Length == witziInfo.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool HasExpectedDimensions(string path)
    {
        try
        {
            var dimensions = _imageProcessor.GetImageDimensions(path);
            return dimensions.Width == WitziPosterFiles.PosterWidth
                && dimensions.Height == WitziPosterFiles.PosterHeight;
        }
        catch
        {
            return false;
        }
    }
}

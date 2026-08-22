namespace Jellyfin.Plugin.WitziEpisodePosters;

/// <summary>
/// Locates the dedicated Witzi poster that belongs to an episode's video file.
/// </summary>
internal static class WitziPosterFiles
{
    /// <summary>
    /// Suffix for the reusable Witzi poster kept beside the episode video.
    /// </summary>
    internal const string PosterSuffix = "-witzi.jpg";

    /// <summary>
    /// Gets the path the poster is installed to and registered as the episode's Primary
    /// image: Jellyfin's recognized "image beside the video" sidecar name.
    /// </summary>
    /// <remarks>
    /// Both halves of the plugin have to name this file the same way. The scheduled task
    /// installs the poster here and records it as Primary, while the local image provider
    /// offers it during a library scan. If they disagreed, the provider would win the scan's
    /// image merge with one path and the task would re-register the other, so each run would
    /// undo the previous one and re-save every episode that has a poster.
    /// </remarks>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <returns>The installed poster path, or <c>null</c> when none can be formed.</returns>
    internal static string? GetInstalledPosterPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath) + ".jpg");
    }

    /// <summary>
    /// Gets the paths a Witzi poster for <paramref name="mediaPath"/> may occupy,
    /// most preferred first. The media directory comes before the metadata
    /// subdirectory because only a file stored with the media marks the Primary
    /// image as locally provided, which is what keeps a remote fetcher from
    /// downloading an episode screenshot over it during a library scan.
    /// </summary>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <returns>Candidate poster paths, or an empty list when none can be formed.</returns>
    internal static IReadOnlyList<string> GetPosterPaths(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return [];
        }

        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(directory))
        {
            return [];
        }

        var fileName = Path.GetFileNameWithoutExtension(mediaPath) + PosterSuffix;
        return
        [
            Path.Combine(directory, fileName),
            Path.Combine(directory, "metadata", fileName)
        ];
    }
}

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

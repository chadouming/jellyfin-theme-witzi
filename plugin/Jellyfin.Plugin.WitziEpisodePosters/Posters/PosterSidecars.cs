using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.WitziEpisodePosters.Posters;

/// <summary>
/// Handles the sidecar images Jellyfin's own episode local-image provider would
/// otherwise offer as Primary, and names the copy this plugin installs.
/// </summary>
internal static class PosterSidecars
{
    // Directory listing matched extensions case-insensitively, so keep the
    // upper-case spelling reachable for case-sensitive media volumes.
    private static readonly string[] PrimarySidecarExtensions = BaseItem.SupportedImageExtensions
        .SelectMany(extension => new[] { extension, extension.ToUpperInvariant() })
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Gets the path the poster is installed to and registered as Primary.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="Providers.WitziEpisodeImageProvider"/> through
    /// <see cref="WitziPosterFiles.GetInstalledPosterPath"/>: both halves must register and
    /// offer the same file, or each scan rewrites the other's choice and re-saves every episode.
    /// </remarks>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <returns>The installed poster path.</returns>
    internal static string GetPrimaryPosterPath(string mediaPath)
    {
        return WitziPosterFiles.GetInstalledPosterPath(mediaPath)
            ?? throw new ArgumentException("The media path has no containing directory.", nameof(mediaPath));
    }

    /// <summary>
    /// Gets the existing sidecars Jellyfin would offer as an episode's Primary image.
    /// </summary>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <returns>Sidecar paths that exist on disk.</returns>
    internal static IEnumerable<string> GetProviderPrimarySidecars(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(directory))
        {
            return [];
        }

        var mediaName = Path.GetFileNameWithoutExtension(mediaPath);
        var sidecars = new List<string>();

        // Probe the fixed set of names Jellyfin recognizes rather than listing
        // the directory. Listing costs one pass over every file in the folder
        // and runs for each episode, so a flat library folder made this
        // quadratic and dominated reruns that had no posters left to build.
        foreach (var searchDirectory in new[] { directory, Path.Combine(directory, "metadata") })
        {
            if (!Directory.Exists(searchDirectory))
            {
                continue;
            }

            foreach (var candidateName in new[] { mediaName, mediaName + "-thumb" })
            {
                foreach (var extension in PrimarySidecarExtensions)
                {
                    var candidate = Path.Combine(searchDirectory, candidateName + extension);
                    if (File.Exists(candidate))
                    {
                        sidecars.Add(candidate);
                    }
                }
            }
        }

        return sidecars;
    }

    /// <summary>
    /// Moves a sidecar aside under a "-witzi-original" name so installing the poster never
    /// destroys artwork that was already there.
    /// </summary>
    /// <param name="path">Sidecar to preserve.</param>
    /// <param name="runLog">Run log.</param>
    internal static void PreserveOriginalSidecar(string path, PosterRunLog runLog)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var backupPath = Path.Combine(directory, name + "-witzi-original" + extension);
        var suffix = 2;

        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(directory, $"{name}-witzi-original-{suffix}{extension}");
            suffix++;
        }

        File.Move(path, backupPath, false);
        runLog.Information(
            $"Preserved previous episode Primary sidecar {path} at {backupPath}");
    }
}

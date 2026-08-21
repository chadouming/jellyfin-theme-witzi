using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Re-selects Witzi posters after each library scan.
/// </summary>
/// <remarks>
/// The local image provider offers the poster on every refresh, but whether it
/// wins depends on Jellyfin's merge: images are ordered by the library's
/// configured image fetcher order before provider order, so a library that pins
/// an explicit order can still resolve Primary to something else. Running after
/// the scan converges regardless of how that merge landed, and costs nothing on
/// an episode that is already correct.
///
/// This never generates artwork. An episode with no existing Witzi poster is
/// left untouched, so a scan never triggers FFmpeg.
/// </remarks>
public sealed class ReassertWitziPostersTask : ILibraryPostScanTask
{
    private readonly GenerateEpisodePostersTask _posterTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReassertWitziPostersTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="mediaEncoder">Media encoder.</param>
    /// <param name="imageProcessor">Image processor.</param>
    /// <param name="applicationPaths">Application paths.</param>
    public ReassertWitziPostersTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        IImageProcessor imageProcessor,
        IApplicationPaths applicationPaths)
    {
        _posterTask = new GenerateEpisodePostersTask(
            libraryManager,
            mediaSourceManager,
            mediaEncoder,
            imageProcessor,
            applicationPaths);
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _posterTask.ReassertExistingPostersAsync(progress, cancellationToken);
    }
}

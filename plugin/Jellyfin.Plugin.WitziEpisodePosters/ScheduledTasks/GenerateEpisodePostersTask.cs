using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WitziEpisodePosters.Posters;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Generates dedicated portrait sidecar images for episodes and registers them as Primary artwork.
/// </summary>
/// <remarks>
/// This type owns the walk over the library and the decision made for each episode. The work
/// itself lives in the Posters namespace: <see cref="PosterInspector"/> decides whether an
/// episode already has a current poster, <see cref="PosterComposer"/> builds one, and
/// <see cref="PosterActivator"/> installs it as the Primary image.
/// </remarks>
public sealed class GenerateEpisodePostersTask : IScheduledTask
{
    private const string ScanLogFileName = "witzi-episode-posters-scan.log";
    private const int MaxConcurrentEpisodes = 4;

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly PosterInspector _inspector;
    private readonly PosterComposer _composer;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateEpisodePostersTask"/> class.
    /// </summary>
    public GenerateEpisodePostersTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        IImageProcessor imageProcessor,
        IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _inspector = new PosterInspector(imageProcessor);
        _composer = new PosterComposer(mediaSourceManager, mediaEncoder);
    }

    /// <inheritdoc />
    public string Name => "Generate Witzi episode posters";

    /// <inheritdoc />
    public string Key => "GenerateWitziEpisodePosters";

    /// <inheritdoc />
    public string Description => "Creates dedicated 2:3 Witzi episode posters, reuses existing Witzi artwork, and registers it as the Primary image.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var runLog = PosterRunLog.Create(_applicationPaths.LogDirectoryPath);
        var episodeIds = _libraryManager.GetItemIds(BuildEpisodeQuery());
        var episodeCount = episodeIds.Count;
        runLog.Information($"Starting Witzi poster generation for {episodeCount} episodes.");
        if (episodeCount == 0)
        {
            runLog.Information("No eligible library episodes were found by the Jellyfin item query.");
            progress.Report(100);
            return;
        }

        var completed = 0;
        var outcomeCounts = new int[Enum.GetValues<EpisodeOutcome>().Length];
        var progressLock = new object();
        var posterGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxConcurrentEpisodes
        };

        runLog.Information(
            $"Using up to {MaxConcurrentEpisodes} concurrent episode workers.");

        try
        {
            await Parallel.ForEachAsync(
                episodeIds,
                parallelOptions,
                async (episodeId, episodeCancellationToken) =>
                {
                    var episode = _libraryManager.GetItemById<Episode>(episodeId);
                    EpisodeOutcome outcome;
                    if (episode is null)
                    {
                        runLog.Warning(
                            $"Skipping {episodeId:N}: the episode left the library after the run started.");
                        outcome = EpisodeOutcome.SkippedIneligible;
                    }
                    else
                    {
                        try
                        {
                            outcome = await ProcessEpisode(
                                episode,
                                runLog,
                                posterGates,
                                episodeCancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            outcome = EpisodeOutcome.Failed;
                            runLog.Error(
                                $"Could not generate a Witzi poster for {episode.Name} at {episode.Path}",
                                ex);
                        }
                    }

                    Interlocked.Increment(ref outcomeCounts[(int)outcome]);
                    lock (progressLock)
                    {
                        completed++;
                        progress.Report(100d * completed / episodeCount);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            foreach (var gate in posterGates.Values)
            {
                gate.Dispose();
            }
        }

        runLog.Information(
            "Completed Witzi poster generation. "
            + string.Join(
                ", ",
                Enum.GetValues<EpisodeOutcome>().Select(outcome =>
                    $"{OutcomeLabel(outcome)}={outcomeCounts[(int)outcome]}")));
        progress.Report(100);
    }

    /// <summary>
    /// Re-selects Witzi posters that a library scan replaced. Jellyfin rebuilds
    /// every image choice from what its providers return, so an episode can lose
    /// artwork this plugin already installed. This never generates anything: an
    /// episode with no existing Witzi poster is left exactly as it is.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the pass.</returns>
    internal async Task ReassertExistingPostersAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        using var runLog = PosterRunLog.Create(_applicationPaths.LogDirectoryPath, ScanLogFileName);
        var episodeIds = _libraryManager.GetItemIds(BuildEpisodeQuery());
        runLog.Information($"Checking {episodeIds.Count} episodes for Witzi posters replaced during the scan.");

        var restored = 0;
        var deferred = 0;
        var current = 0;
        var withoutPoster = 0;
        var completed = 0;

        foreach (var episodeId in episodeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed++;
            progress.Report(100d * completed / Math.Max(episodeIds.Count, 1));

            var episode = _libraryManager.GetItemById<Episode>(episodeId);
            if (episode is null || GetIneligibleReason(episode) is not null)
            {
                continue;
            }

            try
            {
                var mediaPath = episode.Path!;
                var existingPosterPath = _inspector.FindExistingWitziPoster(episode, mediaPath);
                if (existingPosterPath is null)
                {
                    withoutPoster++;
                    continue;
                }

                if (_inspector.IsPersistentWitziPrimary(episode, mediaPath, existingPosterPath))
                {
                    current++;
                    continue;
                }

                var activation = await PosterActivator.Activate(
                    episode,
                    mediaPath,
                    existingPosterPath,
                    runLog,
                    cancellationToken).ConfigureAwait(false);
                if (activation.Registered)
                {
                    restored++;
                    runLog.Information($"Restored the Witzi poster replaced during the scan for {mediaPath}");
                }
                else
                {
                    deferred++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                runLog.Error($"Could not restore the Witzi poster for {episode.Path}", ex);
            }
        }

        // A non-zero restored count means the scan is still taking posters back,
        // which is the number worth watching after a library scan.
        runLog.Information(
            $"Completed the post-scan Witzi poster check. restored={restored}, deferred={deferred}, already-current={current}, no-witzi-poster={withoutPoster}");
        progress.Report(100);
    }

    // Take one unpaged id snapshot. An unsorted Jellyfin item query carries
    // no ORDER BY, so a StartIndex walk is only as stable as the storage
    // order. Registering artwork rewrites the very rows being paged, which
    // can shift episodes across an offset boundary and skip them entirely.
    private static InternalItemsQuery BuildEpisodeQuery()
    {
        return new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IsFolder = false,
            Recursive = true
        };
    }

    private static string? GetIneligibleReason(Episode episode)
    {
        if (!episode.IsFileProtocol)
        {
            return "the media does not use Jellyfin's local file protocol";
        }

        if (episode.IsShortcut)
        {
            return "the episode is a shortcut";
        }

        if (episode.IsPlaceHolder)
        {
            return "the episode is a placeholder";
        }

        if (!episode.IsCompleteMedia)
        {
            return "the episode is an active or incomplete recording";
        }

        if (episode.VideoType == VideoType.Dvd)
        {
            return "DVD media is not supported";
        }

        if (string.IsNullOrWhiteSpace(episode.Path))
        {
            return "the episode has no media path";
        }

        return File.Exists(episode.Path)
            ? null
            : "the media file does not exist from the Jellyfin server's perspective";
    }

    private async Task<EpisodeOutcome> ProcessEpisode(
        Episode episode,
        PosterRunLog runLog,
        ConcurrentDictionary<string, SemaphoreSlim> posterGates,
        CancellationToken cancellationToken)
    {
        var ineligibleReason = GetIneligibleReason(episode);
        if (ineligibleReason is not null)
        {
            runLog.Warning($"Skipping {episode.Name} at {episode.Path}: {ineligibleReason}.");
            return EpisodeOutcome.SkippedIneligible;
        }

        var mediaPath = episode.Path!;
        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(directory))
        {
            runLog.Warning($"Skipping {episode.Name} at {mediaPath}: the media path has no containing directory.");
            return EpisodeOutcome.SkippedIneligible;
        }

        // Keep a dedicated Witzi source so it can be identified and reused.
        // PosterActivator also installs a copy at <video basename>.jpg because
        // Jellyfin's episode local-image provider only persists basename and
        // -thumb sidecars across metadata refreshes.
        var posterPath = WitziPosterFiles.GetPosterPaths(mediaPath)[0];

        // A multi-episode file gives several episodes one video and therefore
        // one poster path. Concurrent workers would each compose the poster and
        // all but one would fail installing it, so hold the path while working.
        var gate = posterGates.GetOrAdd(PosterPaths.NormalizePath(posterPath), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProcessEligibleEpisode(
                episode,
                mediaPath,
                posterPath,
                runLog,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EpisodeOutcome> ProcessEligibleEpisode(
        Episode episode,
        string mediaPath,
        string posterPath,
        PosterRunLog runLog,
        CancellationToken cancellationToken)
    {
        var existingPosterPath = _inspector.FindExistingWitziPoster(episode, mediaPath);

        if (existingPosterPath is not null)
        {
            if (_inspector.IsPersistentWitziPrimary(episode, mediaPath, existingPosterPath))
            {
                runLog.Debug($"Already current: {mediaPath}");
                return EpisodeOutcome.AlreadyCurrent;
            }

            var reactivation = await PosterActivator.Activate(
                episode,
                mediaPath,
                existingPosterPath,
                runLog,
                cancellationToken).ConfigureAwait(false);
            runLog.Information(
                $"Registered existing Witzi episode poster {reactivation.PrimaryPosterPath} as Primary for {mediaPath}");
            return reactivation.Registered
                ? EpisodeOutcome.ReactivatedExisting
                : EpisodeOutcome.RegistrationDeferred;
        }

        if (File.Exists(posterPath))
        {
            // A file using Witzi's reserved name should never be overwritten,
            // even if it is corrupt or was placed there by another process.
            runLog.Warning(
                $"Leaving unrecognized Witzi sidecar untouched at {posterPath}. Move or rename it before rerunning the task.");
            return EpisodeOutcome.SkippedReservedSidecar;
        }

        var videoStream = _composer.GetVideoStream(episode);
        if (videoStream is null)
        {
            runLog.Warning($"Skipping {mediaPath}: no video stream is available.");
            return EpisodeOutcome.SkippedNoVideoStream;
        }

        var extractedFrames = new List<string>(PosterComposer.FramePositions.Length);
        try
        {
            foreach (var position in PosterComposer.FramePositions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var frame = await _composer.ExtractFrame(
                        episode,
                        videoStream,
                        position,
                        runLog,
                        cancellationToken).ConfigureAwait(false);
                    if (File.Exists(frame))
                    {
                        extractedFrames.Add(frame);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    runLog.Warning(
                        $"Frame extraction at {position:P0} failed for {mediaPath}",
                        ex);
                }
            }

            if (extractedFrames.Count == 0)
            {
                runLog.Warning($"No usable frames could be extracted from {mediaPath}.");
                return EpisodeOutcome.FailedNoFrames;
            }

            await _composer.WritePoster(extractedFrames, posterPath, runLog, cancellationToken).ConfigureAwait(false);
            var activation = await PosterActivator.Activate(
                episode,
                mediaPath,
                posterPath,
                runLog,
                cancellationToken).ConfigureAwait(false);
            runLog.Information($"Generated Witzi episode poster {posterPath}");
            runLog.Debug($"Installed Witzi Primary sidecar {activation.PrimaryPosterPath}");
            return activation.Registered
                ? EpisodeOutcome.Generated
                : EpisodeOutcome.RegistrationDeferred;
        }
        finally
        {
            foreach (var frame in extractedFrames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                PosterPaths.TryDelete(frame);
            }
        }
    }

    private static string OutcomeLabel(EpisodeOutcome outcome)
    {
        return outcome switch
        {
            EpisodeOutcome.Generated => "generated",
            EpisodeOutcome.ReactivatedExisting => "reactivated-existing",
            EpisodeOutcome.AlreadyCurrent => "already-current",
            EpisodeOutcome.SkippedIneligible => "skipped-ineligible",
            EpisodeOutcome.SkippedReservedSidecar => "skipped-reserved-sidecar",
            EpisodeOutcome.SkippedNoVideoStream => "skipped-no-video-stream",
            EpisodeOutcome.FailedNoFrames => "failed-no-frames",
            EpisodeOutcome.RegistrationDeferred => "registration-deferred",
            EpisodeOutcome.Failed => "failed",
            _ => outcome.ToString()
        };
    }

    private enum EpisodeOutcome
    {
        Generated,
        ReactivatedExisting,
        AlreadyCurrent,
        SkippedIneligible,
        SkippedReservedSidecar,
        SkippedNoVideoStream,
        FailedNoFrames,
        RegistrationDeferred,
        Failed
    }
}

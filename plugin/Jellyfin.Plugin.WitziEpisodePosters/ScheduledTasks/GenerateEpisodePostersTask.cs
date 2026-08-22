using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Generates dedicated portrait sidecar images for episodes and registers them as Primary artwork.
/// </summary>
public sealed class GenerateEpisodePostersTask : IScheduledTask
{
    private const string ScanLogFileName = "witzi-episode-posters-scan.log";
    private const int PosterWidth = 1000;
    private const int PosterHeight = 1500;
    private const int MaxConcurrentEpisodes = 4;
    private static readonly double[] FramePositions = [0.18d, 0.50d, 0.82d];
    private static readonly string[] PrimarySidecarExtensions = BuildPrimarySidecarExtensions();

    // Saving an item writes its genres, studios, and tags as shared ItemValues
    // rows, and Jellyfin looks a row up before inserting it. Two episodes of one
    // series saved at the same time therefore race to insert the same value and
    // trip the unique index on (Type, Value). SQLite never showed it because
    // Jellyfin serializes writes there; PostgreSQL runs them concurrently.
    // Frame extraction is the expensive part and stays parallel. Only the
    // repository write is serialized, and it is shared by every instance of this
    // task so the post-scan pass cannot race the generation run either.
    private static readonly SemaphoreSlim RepositoryGate = new(1, 1);
    private static readonly string[] BorderColorPalette =
    [
        "CBA6F7", // Mauve
        "89B4FA", // Blue
        "A6E3A1", // Green
        "F38BA8", // Red
        "FAB387", // Peach
        "F9E2AF", // Yellow
        "94E2D5", // Teal
        "89DCEB"  // Sky
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IImageProcessor _imageProcessor;
    private readonly IApplicationPaths _applicationPaths;

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
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _imageProcessor = imageProcessor;
        _applicationPaths = applicationPaths;
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
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IsFolder = false,
            Recursive = true
        };

        // Take one unpaged id snapshot. An unsorted Jellyfin item query carries
        // no ORDER BY, so a StartIndex walk is only as stable as the storage
        // order. Registering artwork rewrites the very rows being paged, which
        // can shift episodes across an offset boundary and skip them entirely.
        var episodeIds = _libraryManager.GetItemIds(query);
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
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IsFolder = false,
            Recursive = true
        };

        var episodeIds = _libraryManager.GetItemIds(query);
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
                var existingPosterPath = FindExistingWitziPoster(episode, mediaPath);
                if (existingPosterPath is null)
                {
                    withoutPoster++;
                    continue;
                }

                if (IsPersistentWitziPrimary(episode, mediaPath, existingPosterPath))
                {
                    current++;
                    continue;
                }

                var activation = await ActivatePoster(
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
        // ActivatePoster also installs a copy at <video basename>.jpg because
        // Jellyfin's episode local-image provider only persists basename and
        // -thumb sidecars across metadata refreshes.
        var posterPath = WitziPosterFiles.GetPosterPaths(mediaPath)[0];

        // A multi-episode file gives several episodes one video and therefore
        // one poster path. Concurrent workers would each compose the poster and
        // all but one would fail installing it, so hold the path while working.
        var gate = posterGates.GetOrAdd(NormalizePath(posterPath), _ => new SemaphoreSlim(1, 1));
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
        var existingPosterPath = FindExistingWitziPoster(episode, mediaPath);

        if (existingPosterPath is not null)
        {
            if (IsPersistentWitziPrimary(episode, mediaPath, existingPosterPath))
            {
                runLog.Debug($"Already current: {mediaPath}");
                return EpisodeOutcome.AlreadyCurrent;
            }

            var activation = await ActivatePoster(
                episode,
                mediaPath,
                existingPosterPath,
                runLog,
                cancellationToken).ConfigureAwait(false);
            runLog.Information(
                $"Registered existing Witzi episode poster {activation.PrimaryPosterPath} as Primary for {mediaPath}");
            return activation.Registered
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

        var videoStream = GetVideoStream(episode);
        if (videoStream is null)
        {
            runLog.Warning($"Skipping {mediaPath}: no video stream is available.");
            return EpisodeOutcome.SkippedNoVideoStream;
        }

        var extractedFrames = new List<string>(FramePositions.Length);
        try
        {
            foreach (var position in FramePositions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var frame = await ExtractFrame(
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

            await WritePoster(extractedFrames, posterPath, runLog, cancellationToken).ConfigureAwait(false);
            var activation = await ActivatePoster(
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
                TryDelete(frame);
            }
        }
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

    private string? FindExistingWitziPoster(Episode episode, string mediaPath)
    {
        var candidates = GetWitziPosterPaths(mediaPath).ToArray();
        var primary = episode.GetImageInfo(ImageType.Primary, 0);
        if (primary is not null
            && candidates.Any(candidate => PathsEqual(primary.Path, candidate))
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

    private static IEnumerable<string> GetWitziPosterPaths(string mediaPath)
    {
        return WitziPosterFiles.GetPosterPaths(mediaPath);
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
            && PathsEqual(primary.Path, legacyPath)
            && File.Exists(legacyPath)
            && HasExpectedDimensions(primary, legacyPath)
                ? legacyPath
                : null;
    }

    private bool IsPersistentWitziPrimary(Episode episode, string mediaPath, string witziPosterPath)
    {
        var primaryPosterPath = GetPrimaryPosterPath(mediaPath);
        var primary = episode.GetImageInfo(ImageType.Primary, 0);
        return primary is not null
            && PathsEqual(primary.Path, primaryPosterPath)
            && (PathsEqual(witziPosterPath, primaryPosterPath)
                || IsUnmodifiedSinceRegistration(primaryPosterPath, witziPosterPath, primary)
                || FilesEqual(witziPosterPath, primaryPosterPath))
            && GetProviderPrimarySidecars(mediaPath).All(path => PathsEqual(path, primaryPosterPath));
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

    private async Task<PosterActivation> ActivatePoster(
        Episode episode,
        string mediaPath,
        string witziPosterPath,
        PosterRunLog runLog,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var primaryPosterPath = GetPrimaryPosterPath(mediaPath);

        foreach (var sidecarPath in GetProviderPrimarySidecars(mediaPath))
        {
            if (PathsEqual(sidecarPath, primaryPosterPath)
                && (PathsEqual(sidecarPath, witziPosterPath) || FilesEqual(sidecarPath, witziPosterPath)))
            {
                continue;
            }

            PreserveOriginalSidecar(sidecarPath, runLog);
        }

        if (!PathsEqual(witziPosterPath, primaryPosterPath))
        {
            if (!File.Exists(primaryPosterPath) || !FilesEqual(primaryPosterPath, witziPosterPath))
            {
                CopyPosterAtomically(witziPosterPath, primaryPosterPath);
            }
        }

        var registered = await TryRegisterPoster(episode, primaryPosterPath, runLog, cancellationToken).ConfigureAwait(false);
        return new PosterActivation(primaryPosterPath, registered);
    }

    private static string GetPrimaryPosterPath(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath)
            ?? throw new ArgumentException("The media path has no containing directory.", nameof(mediaPath));
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath) + ".jpg");
    }

    private static IEnumerable<string> GetProviderPrimarySidecars(string mediaPath)
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

    // Directory listing matched extensions case-insensitively, so keep the
    // upper-case spelling reachable for case-sensitive media volumes.
    private static string[] BuildPrimarySidecarExtensions()
    {
        return BaseItem.SupportedImageExtensions
            .SelectMany(extension => new[] { extension, extension.ToUpperInvariant() })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void PreserveOriginalSidecar(string path, PosterRunLog runLog)
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

    private static void CopyPosterAtomically(string sourcePath, string destinationPath)
    {
        var temporaryPath = destinationPath + ".witzi-install-" + Guid.NewGuid().ToString("N") + ".tmp.jpg";

        try
        {
            File.Copy(sourcePath, temporaryPath, false);
            File.Move(temporaryPath, destinationPath, false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        if (PathsEqual(first, second))
        {
            return true;
        }

        try
        {
            var firstInfo = new FileInfo(first);
            var secondInfo = new FileInfo(second);
            if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            using var firstStream = File.OpenRead(first);
            using var secondStream = File.OpenRead(second);
            return StreamsEqual(firstStream, secondStream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool StreamsEqual(Stream first, Stream second)
    {
        Span<byte> firstBuffer = stackalloc byte[8192];
        Span<byte> secondBuffer = stackalloc byte[8192];

        while (true)
        {
            // Read may legally return less than the buffer without being at the
            // end of the file, which network shares holding media folders do
            // regularly. Filling both buffers first keeps a short read on one
            // side from looking like a content difference, which would send an
            // intact poster through sidecar preservation on every run.
            var firstRead = first.ReadAtLeast(firstBuffer, firstBuffer.Length, false);
            var secondRead = second.ReadAtLeast(secondBuffer, secondBuffer.Length, false);
            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!firstBuffer[..firstRead].SequenceEqual(secondBuffer[..secondRead]))
            {
                return false;
            }
        }
    }

    private bool HasExpectedDimensions(ItemImageInfo image, string path)
    {
        if (image.Width > 0 && image.Height > 0)
        {
            return image.Width == PosterWidth && image.Height == PosterHeight;
        }

        return HasExpectedDimensions(path);
    }

    private bool HasExpectedDimensions(string path)
    {
        try
        {
            var dimensions = _imageProcessor.GetImageDimensions(path);
            return dimensions.Width == PosterWidth && dimensions.Height == PosterHeight;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private MediaStream? GetVideoStream(Episode episode)
    {
        var query = new MediaStreamQuery
        {
            ItemId = episode.Id,
            Index = episode.DefaultVideoStreamIndex,
            Type = episode.DefaultVideoStreamIndex.HasValue ? null : MediaStreamType.Video
        };

        var stream = _mediaSourceManager.GetMediaStreams(query).FirstOrDefault();
        if (stream is not null)
        {
            return stream;
        }

        query.Index = null;
        query.Type = MediaStreamType.Video;
        return _mediaSourceManager.GetMediaStreams(query).FirstOrDefault();
    }

    private async Task<string> ExtractFrame(
        Episode episode,
        MediaStream videoStream,
        double position,
        PosterRunLog runLog,
        CancellationToken cancellationToken)
    {
        var offset = episode.RunTimeTicks is > 0
            ? TimeSpan.FromTicks((long)(episode.RunTimeTicks.Value * position))
            : TimeSpan.FromSeconds(10 + (position * 60));

        var hardwareFrame = await TryExtractFrameWithHardwareAcceleration(
            episode,
            videoStream,
            offset,
            runLog,
            cancellationToken).ConfigureAwait(false);
        if (hardwareFrame is not null)
        {
            return hardwareFrame;
        }

        var mediaSource = new MediaSourceInfo
        {
            VideoType = episode.VideoType,
            IsoType = episode.IsoType,
            Protocol = episode.PathProtocol ?? MediaProtocol.File
        };

        return await _mediaEncoder.ExtractVideoImage(
            episode.Path!,
            episode.Container ?? string.Empty,
            mediaSource,
            videoStream,
            episode.Video3DFormat,
            offset,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryExtractFrameWithHardwareAcceleration(
        Episode episode,
        MediaStream videoStream,
        TimeSpan offset,
        PosterRunLog runLog,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath))
        {
            return null;
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "witzi-frame-" + Guid.NewGuid().ToString("N") + ".jpg");

        try
        {
            var startInfo = CreateFfmpegStartInfo();
            startInfo.ArgumentList.Add("-hwaccel");
            startInfo.ArgumentList.Add("auto");
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(episode.Path!);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add($"0:{videoStream.Index}");
            startInfo.ArgumentList.Add("-an");
            startInfo.ArgumentList.Add("-sn");
            startInfo.ArgumentList.Add("-dn");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add(outputPath);

            var result = await RunFfmpeg(startInfo, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 && File.Exists(outputPath))
            {
                runLog.Debug(
                    $"Extracted a poster frame with FFmpeg hardware acceleration when available for {episode.Path}");
                return outputPath;
            }

            runLog.Debug(
                $"Hardware-accelerated frame extraction failed for {episode.Path}; retrying with Jellyfin's software extraction. FFmpeg: {result.Error}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            runLog.Debug(
                $"Hardware-accelerated frame extraction could not start for {episode.Path}; retrying with Jellyfin's software extraction",
                ex);
        }

        TryDelete(outputPath);
        return null;
    }

    private async Task WritePoster(
        IReadOnlyList<string> framePaths,
        string outputPath,
        PosterRunLog runLog,
        CancellationToken cancellationToken)
    {
        var temporaryPath = outputPath + ".witzi-" + Guid.NewGuid().ToString("N") + ".tmp.jpg";

        try
        {
            if (string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath))
            {
                throw new InvalidOperationException("Jellyfin does not have a configured FFmpeg encoder path.");
            }

            var selectedFrames = Enumerable.Range(0, 3)
                .Select(index => framePaths[index % framePaths.Count])
                .ToArray();

            var startInfo = CreateFfmpegStartInfo();

            foreach (var frame in selectedFrames)
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(frame);
            }

            var borderColors = GetRandomBorderColors();
            var filter =
                "[1:v]scale=1000:1500:force_original_aspect_ratio=increase,crop=1000:1500,gblur=sigma=42,eq=brightness=-0.30:saturation=0.78[bg];" +
                "[bg]drawbox=x=82:y=101:w=860:h=370:color=black@0.45:t=fill," +
                "drawbox=x=82:y=581:w=860:h=370:color=black@0.45:t=fill," +
                "drawbox=x=82:y=1061:w=860:h=370:color=black@0.45:t=fill[base];" +
                "[0:v]scale=860:370:force_original_aspect_ratio=increase,crop=860:370[p0];" +
                "[1:v]scale=860:370:force_original_aspect_ratio=increase,crop=860:370[p1];" +
                "[2:v]scale=860:370:force_original_aspect_ratio=increase,crop=860:370[p2];" +
                "[base][p0]overlay=70:85[v0];" +
                $"[v0]drawbox=x=70:y=85:w=860:h=370:color=0x{borderColors[0]}@0.96:t=7[v1];" +
                "[v1][p1]overlay=70:565[v2];" +
                $"[v2]drawbox=x=70:y=565:w=860:h=370:color=0x{borderColors[1]}@0.96:t=7[v3];" +
                "[v3][p2]overlay=70:1045[v4];" +
                $"[v4]drawbox=x=70:y=1045:w=860:h=370:color=0x{borderColors[2]}@0.96:t=7,format=yuvj420p[out]";

            runLog.Debug(
                $"Using randomized Witzi border colors {string.Join(", ", borderColors)} for {outputPath}");

            startInfo.ArgumentList.Add("-filter_complex");
            startInfo.ArgumentList.Add(filter);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("[out]");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add(temporaryPath);

            var result = await RunFfmpeg(startInfo, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(temporaryPath))
            {
                throw new InvalidOperationException($"FFmpeg poster composition failed with exit code {result.ExitCode}: {result.Error}");
            }

            File.Move(temporaryPath, outputPath, false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private ProcessStartInfo CreateFfmpegStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        return startInfo;
    }

    private static string[] GetRandomBorderColors()
    {
        var colors = BorderColorPalette.ToArray();
        for (var index = colors.Length - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (colors[index], colors[swapIndex]) = (colors[swapIndex], colors[index]);
        }

        return colors[..3];
    }

    private static async Task<FfmpegResult> RunFfmpeg(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Jellyfin's FFmpeg process could not be started.");
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            throw;
        }

        return new FfmpegResult(process.ExitCode, await errorTask.ConfigureAwait(false));
    }

    // The poster file and its sidecar copy are fully installed before this
    // runs, and the local image provider offers the poster on every refresh
    // while the post-scan pass re-selects it. A write that keeps losing to
    // Jellyfin's own concurrent save of the same item therefore costs the
    // immediate update, not the poster, so it is reported rather than thrown.
    private static async Task<bool> TryRegisterPoster(
        Episode episode,
        string posterPath,
        PosterRunLog runLog,
        CancellationToken cancellationToken)
    {
        try
        {
            await RegisterPoster(episode, posterPath, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsConcurrentSaveConflict(ex))
        {
            runLog.Warning(
                $"Jellyfin was saving {episode.Path} at the same time, so the poster is installed but not yet selected. The image provider will offer it on the next refresh.",
                ex);
            return false;
        }
    }

    private static async Task RegisterPoster(Episode episode, string posterPath, CancellationToken cancellationToken)
    {
        episode.SetImage(
            new ItemImageInfo
            {
                Type = ImageType.Primary,
                Path = posterPath,
                DateModified = File.GetLastWriteTimeUtc(posterPath),
                Width = PosterWidth,
                Height = PosterHeight
            },
            0);

        await RepositoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveEpisodeWithRetry(episode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RepositoryGate.Release();
        }
    }

    // The gate removes this task from racing itself, but a library scan can be
    // saving the same shared values at the same time and nothing here can
    // serialize that. The losing insert is retried, by which point the value the
    // other writer added already exists and the save succeeds.
    private static async Task SaveEpisodeWithRetry(Episode episode, CancellationToken cancellationToken)
    {
        // The competing writer is a library scan or metadata refresh, not this
        // task, so the wait has to outlast someone else's save rather than a lock.
        const int MaxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await episode.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsConcurrentSaveConflict(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << (attempt - 1))), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsConcurrentSaveConflict(Exception exception)
    {
        // The plugin does not reference Entity Framework, so the conflict is
        // recognized by name and by the constraint the database reports rather
        // than by catching DbUpdateException directly.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name.Contains("DbUpdateException", StringComparison.Ordinal)
                || current.Message.Contains("IX_ItemValues_Type_Value", StringComparison.Ordinal)
                || current.Message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Jellyfin's regular temp cleanup will catch an extracted frame that
            // could not be removed immediately.
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

    private sealed class PosterRunLog : IDisposable
    {
        private const string LogFileName = "witzi-episode-posters.log";
        private readonly object _syncRoot = new();
        private readonly StreamWriter _writer;

        private PosterRunLog(string filePath)
        {
            var stream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite);
            // Buffer routine progress and flush only the lines worth keeping if
            // the server dies mid-run. Flushing every line meant a write syscall
            // per message under the shared lock, with four workers contending.
            _writer = new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = false
            };
        }

        public static PosterRunLog Create(string logDirectoryPath, string fileName = LogFileName)
        {
            Directory.CreateDirectory(logDirectoryPath);
            return new PosterRunLog(Path.Combine(logDirectoryPath, fileName));
        }

        public void Debug(string message, Exception? exception = null)
        {
            Write("DBG", message, exception);
        }

        public void Information(string message)
        {
            Write("INF", message);
        }

        public void Warning(string message, Exception? exception = null)
        {
            Write("WRN", message, exception, flush: true);
        }

        public void Error(string message, Exception exception)
        {
            Write("ERR", message, exception, flush: true);
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                _writer.Dispose();
            }
        }

        private void Write(
            string level,
            string message,
            Exception? exception = null,
            bool flush = false)
        {
            lock (_syncRoot)
            {
                _writer.WriteLine($"{DateTimeOffset.UtcNow:O} [{level}] {message}");
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }

                if (flush)
                {
                    _writer.Flush();
                }
            }
        }
    }

    private readonly record struct FfmpegResult(int ExitCode, string Error);

    private readonly record struct PosterActivation(string PrimaryPosterPath, bool Registered);
}

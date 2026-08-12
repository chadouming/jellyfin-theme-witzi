using System.Diagnostics;
using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Configuration;
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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Generates dedicated portrait sidecar images for episodes and registers them as Primary artwork.
/// </summary>
public sealed class GenerateEpisodePostersTask : IScheduledTask
{
    private const int QueryPageSize = 100;
    private const int PosterWidth = 1000;
    private const int PosterHeight = 1500;
    private static readonly double[] FramePositions = [0.18d, 0.50d, 0.82d];
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
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<GenerateEpisodePostersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateEpisodePostersTask"/> class.
    /// </summary>
    public GenerateEpisodePostersTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        IImageProcessor imageProcessor,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<GenerateEpisodePostersTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _imageProcessor = imageProcessor;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
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
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IsFolder = false,
            Recursive = true,
            Limit = QueryPageSize
        };

        var episodeCount = _libraryManager.GetCount(query);
        if (episodeCount == 0)
        {
            progress.Report(100);
            return;
        }

        var completed = 0;
        var progressLock = new object();
        var configuredParallelLimit = _serverConfigurationManager.Configuration.ParallelImageEncodingLimit;
        var maxConcurrentEpisodes = configuredParallelLimit > 0
            ? configuredParallelLimit
            : Environment.ProcessorCount;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxConcurrentEpisodes
        };

        _logger.LogInformation(
            "Processing {EpisodeCount} episodes with up to {WorkerCount} concurrent Witzi poster workers",
            episodeCount,
            maxConcurrentEpisodes);

        for (var startIndex = 0; startIndex < episodeCount; startIndex += QueryPageSize)
        {
            query.StartIndex = startIndex;
            var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToArray();

            await Parallel.ForEachAsync(
                episodes,
                parallelOptions,
                async (episode, episodeCancellationToken) =>
                {
                    try
                    {
                        await ProcessEpisode(episode, episodeCancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not generate a Witzi poster for {EpisodeName} at {EpisodePath}", episode.Name, episode.Path);
                    }

                    lock (progressLock)
                    {
                        completed++;
                        progress.Report(100d * completed / episodeCount);
                    }
                }).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    private async Task ProcessEpisode(Episode episode, CancellationToken cancellationToken)
    {
        if (!CanProcess(episode))
        {
            return;
        }

        var mediaPath = episode.Path!;
        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        // Keep a dedicated Witzi source so it can be identified and reused.
        // ActivatePoster also installs a copy at <video basename>.jpg because
        // Jellyfin's episode local-image provider only persists basename and
        // -thumb sidecars across metadata refreshes.
        var posterPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath) + "-witzi.jpg");
        var existingPosterPath = FindExistingWitziPoster(episode, mediaPath);

        if (existingPosterPath is not null)
        {
            if (IsPersistentWitziPrimary(episode, mediaPath, existingPosterPath))
            {
                _logger.LogDebug("Skipping {EpisodePath}: its Witzi poster is already Primary", mediaPath);
            }
            else
            {
                var primaryPosterPath = await ActivatePoster(
                    episode,
                    mediaPath,
                    existingPosterPath,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Registered existing Witzi episode poster {PosterPath} as Primary for {EpisodePath}",
                    primaryPosterPath,
                    mediaPath);
            }

            return;
        }

        if (File.Exists(posterPath))
        {
            // A file using Witzi's reserved name should never be overwritten,
            // even if it is corrupt or was placed there by another process.
            _logger.LogWarning(
                "Leaving unrecognized Witzi sidecar untouched at {PosterPath}. Move or rename it before rerunning the task.",
                posterPath);
            return;
        }

        var videoStream = GetVideoStream(episode);
        if (videoStream is null)
        {
            _logger.LogDebug("Skipping {EpisodePath}: no video stream is available", mediaPath);
            return;
        }

        var extractedFrames = new List<string>(FramePositions.Length);
        try
        {
            foreach (var position in FramePositions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var frame = await ExtractFrame(episode, videoStream, position, cancellationToken).ConfigureAwait(false);
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
                    _logger.LogDebug(ex, "Frame extraction at {FramePercent:P0} failed for {EpisodePath}", position, mediaPath);
                }
            }

            if (extractedFrames.Count == 0)
            {
                _logger.LogWarning("No usable frames could be extracted from {EpisodePath}", mediaPath);
                return;
            }

            await WritePoster(extractedFrames, posterPath, cancellationToken).ConfigureAwait(false);
            var primaryPosterPath = await ActivatePoster(
                episode,
                mediaPath,
                posterPath,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Generated Witzi episode poster {PosterPath}", posterPath);
            _logger.LogDebug("Installed Witzi Primary sidecar {PrimaryPosterPath}", primaryPosterPath);
        }
        finally
        {
            foreach (var frame in extractedFrames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                TryDelete(frame);
            }
        }
    }

    private static bool CanProcess(Episode episode)
    {
        return episode.IsFileProtocol
            && !episode.IsShortcut
            && !episode.IsPlaceHolder
            && episode.IsCompleteMedia
            && episode.VideoType != VideoType.Dvd
            && !string.IsNullOrWhiteSpace(episode.Path)
            && File.Exists(episode.Path);
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
        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(directory))
        {
            return [];
        }

        var mediaName = Path.GetFileNameWithoutExtension(mediaPath);
        var fileName = mediaName + "-witzi.jpg";
        return
        [
            Path.Combine(directory, fileName),
            Path.Combine(directory, "metadata", fileName)
        ];
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
            && (PathsEqual(witziPosterPath, primaryPosterPath) || FilesEqual(witziPosterPath, primaryPosterPath))
            && GetProviderPrimarySidecars(mediaPath).All(path => PathsEqual(path, primaryPosterPath));
    }

    private async Task<string> ActivatePoster(
        Episode episode,
        string mediaPath,
        string witziPosterPath,
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

            PreserveOriginalSidecar(sidecarPath);
        }

        if (!PathsEqual(witziPosterPath, primaryPosterPath))
        {
            if (!File.Exists(primaryPosterPath) || !FilesEqual(primaryPosterPath, witziPosterPath))
            {
                CopyPosterAtomically(witziPosterPath, primaryPosterPath);
            }
        }

        await RegisterPoster(episode, primaryPosterPath, cancellationToken).ConfigureAwait(false);
        return primaryPosterPath;
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
        var thumbnailName = mediaName + "-thumb";
        var sidecars = new List<string>();

        foreach (var searchDirectory in new[] { directory, Path.Combine(directory, "metadata") })
        {
            if (!Directory.Exists(searchDirectory))
            {
                continue;
            }

            try
            {
                sidecars.AddRange(Directory.GetFiles(searchDirectory).Where(path =>
                {
                    if (!BaseItem.SupportedImageExtensions.Contains(
                            Path.GetExtension(path),
                            StringComparer.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var candidateName = Path.GetFileNameWithoutExtension(path);
                    return string.Equals(candidateName, mediaName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidateName, thumbnailName, StringComparison.OrdinalIgnoreCase);
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Activation will still register the preferred path. A directory
                // that cannot be enumerated also cannot contain a writable
                // conflict that this task can safely preserve.
            }
        }

        return sidecars;
    }

    private void PreserveOriginalSidecar(string path)
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
        _logger.LogInformation(
            "Preserved previous episode Primary sidecar {OriginalPath} at {BackupPath}",
            path,
            backupPath);
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
            var firstRead = first.Read(firstBuffer);
            var secondRead = second.Read(secondBuffer);
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
        CancellationToken cancellationToken)
    {
        var offset = episode.RunTimeTicks is > 0
            ? TimeSpan.FromTicks((long)(episode.RunTimeTicks.Value * position))
            : TimeSpan.FromSeconds(10 + (position * 60));

        var hardwareFrame = await TryExtractFrameWithHardwareAcceleration(
            episode,
            videoStream,
            offset,
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
                _logger.LogDebug(
                    "Extracted a poster frame with FFmpeg hardware acceleration when available for {EpisodePath}",
                    episode.Path);
                return outputPath;
            }

            _logger.LogDebug(
                "Hardware-accelerated frame extraction failed for {EpisodePath}; retrying with Jellyfin's software extraction. FFmpeg: {Error}",
                episode.Path,
                result.Error);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Hardware-accelerated frame extraction could not start for {EpisodePath}; retrying with Jellyfin's software extraction",
                episode.Path);
        }

        TryDelete(outputPath);
        return null;
    }

    private async Task WritePoster(IReadOnlyList<string> framePaths, string outputPath, CancellationToken cancellationToken)
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

            _logger.LogDebug(
                "Using randomized Witzi border colors {BorderColors} for {PosterPath}",
                string.Join(", ", borderColors),
                outputPath);

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

        await episode.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
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

    private readonly record struct FfmpegResult(int ExitCode, string Error);
}

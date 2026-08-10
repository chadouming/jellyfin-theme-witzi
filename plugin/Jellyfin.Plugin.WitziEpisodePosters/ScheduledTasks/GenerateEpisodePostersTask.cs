using System.Diagnostics;
using Jellyfin.Data.Enums;
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
/// Generates portrait sidecar images for episodes that do not already have portrait artwork.
/// </summary>
public sealed class GenerateEpisodePostersTask : IScheduledTask
{
    private const int QueryPageSize = 100;
    private const int PosterWidth = 1000;
    private const int PosterHeight = 1500;
    private static readonly double[] FramePositions = [0.18d, 0.50d, 0.82d];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<GenerateEpisodePostersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateEpisodePostersTask"/> class.
    /// </summary>
    public GenerateEpisodePostersTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        IImageProcessor imageProcessor,
        ILogger<GenerateEpisodePostersTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _imageProcessor = imageProcessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Generate Witzi episode posters";

    /// <inheritdoc />
    public string Key => "GenerateWitziEpisodePosters";

    /// <inheritdoc />
    public string Description => "Creates 2:3 episode posters from three representative video frames and stores them beside each media file.";

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
        for (var startIndex = 0; startIndex < episodeCount; startIndex += QueryPageSize)
        {
            query.StartIndex = startIndex;
            var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToArray();

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ProcessEpisode(episode, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not generate a Witzi poster for {EpisodeName} at {EpisodePath}", episode.Name, episode.Path);
                }

                completed++;
                progress.Report(100d * completed / episodeCount);
            }
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

        // Jellyfin's EpisodeLocalImageProvider recognizes <video basename>.jpg
        // as the episode's Primary image.
        var posterPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath) + ".jpg");

        if (File.Exists(posterPath))
        {
            if (IsPortrait(posterPath))
            {
                await RegisterPoster(episode, posterPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "Leaving existing landscape sidecar untouched at {PosterPath}. Move or rename it before rerunning the task if Witzi should replace it.",
                    posterPath);
            }

            return;
        }

        if (HasPortraitPrimary(episode))
        {
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
            await RegisterPoster(episode, posterPath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Generated Witzi episode poster {PosterPath}", posterPath);
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

    private bool HasPortraitPrimary(Episode episode)
    {
        var image = episode.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            return false;
        }

        if (image.Width > 0 && image.Height > 0)
        {
            return image.Height > image.Width;
        }

        return image.IsLocalFile && File.Exists(image.Path) && IsPortrait(image.Path);
    }

    private bool IsPortrait(string path)
    {
        try
        {
            var dimensions = _imageProcessor.GetImageDimensions(path);
            return dimensions.Height > dimensions.Width;
        }
        catch
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

    private Task<string> ExtractFrame(
        Episode episode,
        MediaStream videoStream,
        double position,
        CancellationToken cancellationToken)
    {
        var mediaSource = new MediaSourceInfo
        {
            VideoType = episode.VideoType,
            IsoType = episode.IsoType,
            Protocol = episode.PathProtocol ?? MediaProtocol.File
        };

        var offset = episode.RunTimeTicks is > 0
            ? TimeSpan.FromTicks((long)(episode.RunTimeTicks.Value * position))
            : TimeSpan.FromSeconds(10 + (position * 60));

        return _mediaEncoder.ExtractVideoImage(
            episode.Path!,
            episode.Container ?? string.Empty,
            mediaSource,
            videoStream,
            episode.Video3DFormat,
            offset,
            cancellationToken);
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

            foreach (var frame in selectedFrames)
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(frame);
            }

            const string Filter =
                "[1:v]scale=1000:1500:force_original_aspect_ratio=increase,crop=1000:1500,gblur=sigma=42,eq=brightness=-0.30:saturation=0.78[bg];" +
                "[bg]drawbox=x=82:y=101:w=860:h=370:color=black@0.45:t=fill," +
                "drawbox=x=82:y=581:w=860:h=370:color=black@0.45:t=fill," +
                "drawbox=x=82:y=1061:w=860:h=370:color=black@0.45:t=fill[base];" +
                "[0:v]scale=860:370:force_original_aspect_ratio=increase,crop=860:370[p0];" +
                "[1:v]scale=860:370:force_original_aspect_ratio=increase,crop=860:370[p1];" +
                "[2:v]scale=860:370:force_original_aspect_ratio=increase,crop=860:370[p2];" +
                "[base][p0]overlay=70:85[v0];" +
                "[v0]drawbox=x=70:y=85:w=860:h=370:color=0xCBA6F7@0.96:t=7[v1];" +
                "[v1][p1]overlay=70:565[v2];" +
                "[v2]drawbox=x=70:y=565:w=860:h=370:color=0x89B4FA@0.96:t=7[v3];" +
                "[v3][p2]overlay=70:1045[v4];" +
                "[v4]drawbox=x=70:y=1045:w=860:h=370:color=0xA6E3A1@0.96:t=7,format=yuvj420p[out]";

            startInfo.ArgumentList.Add("-filter_complex");
            startInfo.ArgumentList.Add(Filter);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("[out]");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-q:v");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add(temporaryPath);

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

            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(temporaryPath))
            {
                throw new InvalidOperationException($"FFmpeg poster composition failed with exit code {process.ExitCode}: {error}");
            }

            File.Move(temporaryPath, outputPath, false);
        }
        finally
        {
            TryDelete(temporaryPath);
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
}

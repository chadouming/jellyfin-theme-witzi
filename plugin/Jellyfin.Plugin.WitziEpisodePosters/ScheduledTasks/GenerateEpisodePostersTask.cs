using Jellyfin.Data.Enums;
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
using SkiaSharp;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Generates portrait sidecar images for episodes that do not already have portrait artwork.
/// </summary>
public sealed class GenerateEpisodePostersTask : IScheduledTask
{
    private const int QueryPageSize = 100;
    private const int PosterWidth = 1000;
    private const int PosterHeight = 1500;
    private const int JpegQuality = 90;

    private static readonly double[] FramePositions = [0.18d, 0.50d, 0.82d];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<GenerateEpisodePostersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateEpisodePostersTask"/> class.
    /// </summary>
    public GenerateEpisodePostersTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        ILogger<GenerateEpisodePostersTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
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
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
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

            WritePoster(extractedFrames, posterPath);
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

    private static bool IsPortrait(string path)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            return bitmap is not null && bitmap.Height > bitmap.Width;
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

    private static void WritePoster(IReadOnlyList<string> framePaths, string outputPath)
    {
        var bitmaps = new List<SKBitmap>(framePaths.Count);
        var temporaryPath = outputPath + ".witzi-" + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            foreach (var path in framePaths)
            {
                var bitmap = SKBitmap.Decode(path);
                if (bitmap is not null)
                {
                    bitmaps.Add(bitmap);
                }
            }

            if (bitmaps.Count == 0)
            {
                throw new InvalidDataException("Skia could not decode any extracted frame.");
            }

            var imageInfo = new SKImageInfo(PosterWidth, PosterHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(imageInfo) ?? throw new InvalidOperationException("Skia could not create the poster canvas.");
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(30, 30, 46));

            var background = bitmaps[Math.Min(1, bitmaps.Count - 1)];
            using (var blurFilter = SKImageFilter.CreateBlur(42, 42))
            using (var blurPaint = new SKPaint { ImageFilter = blurFilter, IsAntialias = true })
            {
                DrawCover(canvas, background, new SKRect(-35, -35, PosterWidth + 35, PosterHeight + 35), blurPaint);
            }

            canvas.DrawColor(new SKColor(24, 24, 37, 178), SKBlendMode.SrcOver);
            DrawPaletteGlows(canvas);

            var panelRects = new[]
            {
                new SKRect(70, 85, 930, 455),
                new SKRect(70, 565, 930, 935),
                new SKRect(70, 1045, 930, 1415)
            };
            var borderColors = new[]
            {
                new SKColor(203, 166, 247),
                new SKColor(137, 180, 250),
                new SKColor(166, 227, 161)
            };

            for (var index = 0; index < panelRects.Length; index++)
            {
                var rect = panelRects[index];
                using (var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 115), IsAntialias = true })
                {
                    canvas.DrawRoundRect(new SKRoundRect(new SKRect(rect.Left + 12, rect.Top + 16, rect.Right + 12, rect.Bottom + 16), 28, 28), shadowPaint);
                }

                canvas.Save();
                canvas.ClipRoundRect(new SKRoundRect(rect, 28, 28), SKClipOperation.Intersect, true);
                DrawCover(canvas, bitmaps[index % bitmaps.Count], rect, null);
                using (var tintPaint = new SKPaint { Color = new SKColor(borderColors[index].Red, borderColors[index].Green, borderColors[index].Blue, 24) })
                {
                    canvas.DrawRect(rect, tintPaint);
                }

                canvas.Restore();

                using var borderPaint = new SKPaint
                {
                    Color = borderColors[index],
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 7
                };
                canvas.DrawRoundRect(new SKRoundRect(rect, 28, 28), borderPaint);
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                ?? throw new InvalidOperationException("Skia could not encode the poster as JPEG.");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                data.SaveTo(stream);
            }

            File.Move(temporaryPath, outputPath, false);
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }

            TryDelete(temporaryPath);
        }
    }

    private static void DrawCover(SKCanvas canvas, SKBitmap bitmap, SKRect destination, SKPaint? paint)
    {
        var scale = Math.Max(destination.Width / bitmap.Width, destination.Height / bitmap.Height);
        var sourceWidth = destination.Width / scale;
        var sourceHeight = destination.Height / scale;
        var source = new SKRect(
            (bitmap.Width - sourceWidth) / 2,
            (bitmap.Height - sourceHeight) / 2,
            (bitmap.Width + sourceWidth) / 2,
            (bitmap.Height + sourceHeight) / 2);

        canvas.DrawBitmap(bitmap, source, destination, paint);
    }

    private static void DrawPaletteGlows(SKCanvas canvas)
    {
        var glows = new[]
        {
            (new SKPoint(90, 240), new SKColor(203, 166, 247, 115)),
            (new SKPoint(930, 760), new SKColor(137, 180, 250, 105)),
            (new SKPoint(130, 1320), new SKColor(166, 227, 161, 95))
        };

        foreach (var (center, color) in glows)
        {
            using var shader = SKShader.CreateRadialGradient(
                center,
                520,
                [color, new SKColor(color.Red, color.Green, color.Blue, 0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            canvas.DrawRect(new SKRect(0, 0, PosterWidth, PosterHeight), paint);
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

using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.WitziEpisodePosters.Posters;

/// <summary>
/// Builds a Witzi poster out of an episode: pulls frames from the video with
/// Jellyfin's FFmpeg and composes them into one 2:3 portrait image.
/// </summary>
internal sealed class PosterComposer
{
    /// <summary>
    /// Points in the episode, as a fraction of its runtime, that frames are taken from. Three
    /// spread-out frames fill the poster's three panels and avoid opening titles and credits.
    /// </summary>
    internal static readonly double[] FramePositions = [0.18d, 0.50d, 0.82d];

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

    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;

    internal PosterComposer(IMediaSourceManager mediaSourceManager, IMediaEncoder mediaEncoder)
    {
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Gets the video stream frames should be taken from.
    /// </summary>
    /// <param name="episode">Episode to read.</param>
    /// <returns>The stream, or <c>null</c> when the episode has none.</returns>
    internal MediaStream? GetVideoStream(Episode episode)
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

    /// <summary>
    /// Extracts one frame to a temporary file, which the caller owns and deletes.
    /// </summary>
    /// <param name="episode">Episode to read.</param>
    /// <param name="videoStream">Video stream to read.</param>
    /// <param name="position">Position in the episode, as a fraction of its runtime.</param>
    /// <param name="runLog">Run log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path of the extracted frame.</returns>
    internal async Task<string> ExtractFrame(
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

    /// <summary>
    /// Composes the extracted frames into the poster, written atomically to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="framePaths">Extracted frames, cycled when fewer than three were usable.</param>
    /// <param name="outputPath">Poster path to write.</param>
    /// <param name="runLog">Run log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the composition.</returns>
    internal async Task WritePoster(
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

            // The middle frame, blurred and darkened, becomes the backdrop; the three frames
            // then sit on it as evenly spaced panels with a randomized Witzi border each. The
            // canvas comes from the shared dimensions because that exact size is what later
            // identifies the file as a Witzi poster; the panel geometry is laid out for it.
            var canvas = $"{WitziPosterFiles.PosterWidth}:{WitziPosterFiles.PosterHeight}";
            var filter =
                $"[1:v]scale={canvas}:force_original_aspect_ratio=increase,crop={canvas},gblur=sigma=42,eq=brightness=-0.30:saturation=0.78[bg];" +
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
            PosterPaths.TryDelete(temporaryPath);
        }
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
            PosterPaths.TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            runLog.Debug(
                $"Hardware-accelerated frame extraction could not start for {episode.Path}; retrying with Jellyfin's software extraction",
                ex);
        }

        PosterPaths.TryDelete(outputPath);
        return null;
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

    private readonly record struct FfmpegResult(int ExitCode, string Error);
}

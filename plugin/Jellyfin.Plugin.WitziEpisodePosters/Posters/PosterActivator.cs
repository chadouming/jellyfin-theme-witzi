using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.WitziEpisodePosters.Posters;

/// <summary>
/// Installs a Witzi poster under the sidecar name Jellyfin recognizes and records it as the
/// episode's Primary image.
/// </summary>
internal static class PosterActivator
{
    // Saving an item writes its genres, studios, and tags as shared ItemValues
    // rows, and Jellyfin looks a row up before inserting it. Two episodes of one
    // series saved at the same time therefore race to insert the same value and
    // trip the unique index on (Type, Value). SQLite never showed it because
    // Jellyfin serializes writes there; PostgreSQL runs them concurrently.
    // Frame extraction is the expensive part and stays parallel. Only the
    // repository write is serialized, and it is shared by every instance of this
    // task so the post-scan pass cannot race the generation run either.
    private static readonly SemaphoreSlim RepositoryGate = new(1, 1);

    /// <summary>
    /// Makes <paramref name="witziPosterPath"/> the episode's Primary image: any sidecar that
    /// would compete for that role is preserved, the poster is copied into the sidecar name,
    /// and the episode is saved pointing at it.
    /// </summary>
    /// <param name="episode">Episode to update.</param>
    /// <param name="mediaPath">Path of the episode video file.</param>
    /// <param name="witziPosterPath">Poster to install.</param>
    /// <param name="runLog">Run log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installed path and whether Jellyfin accepted the write.</returns>
    internal static async Task<PosterActivationResult> Activate(
        Episode episode,
        string mediaPath,
        string witziPosterPath,
        PosterRunLog runLog,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var primaryPosterPath = PosterSidecars.GetPrimaryPosterPath(mediaPath);

        foreach (var sidecarPath in PosterSidecars.GetProviderPrimarySidecars(mediaPath))
        {
            if (PosterPaths.PathsEqual(sidecarPath, primaryPosterPath)
                && (PosterPaths.PathsEqual(sidecarPath, witziPosterPath)
                    || PosterPaths.FilesEqual(sidecarPath, witziPosterPath)))
            {
                continue;
            }

            PosterSidecars.PreserveOriginalSidecar(sidecarPath, runLog);
        }

        if (!PosterPaths.PathsEqual(witziPosterPath, primaryPosterPath)
            && (!File.Exists(primaryPosterPath) || !PosterPaths.FilesEqual(primaryPosterPath, witziPosterPath)))
        {
            PosterPaths.CopyPosterAtomically(witziPosterPath, primaryPosterPath);
        }

        var registered = await TryRegisterPoster(episode, primaryPosterPath, runLog, cancellationToken).ConfigureAwait(false);
        return new PosterActivationResult(primaryPosterPath, registered);
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
                Width = WitziPosterFiles.PosterWidth,
                Height = WitziPosterFiles.PosterHeight
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
}

/// <summary>
/// Outcome of installing a poster.
/// </summary>
/// <param name="PrimaryPosterPath">Path the poster was installed to.</param>
/// <param name="Registered">Whether Jellyfin accepted the write selecting it as Primary.</param>
internal readonly record struct PosterActivationResult(string PrimaryPosterPath, bool Registered);

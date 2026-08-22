namespace Jellyfin.Plugin.WitziEpisodePosters.Posters;

/// <summary>
/// Compares poster paths and their contents, and performs the file moves that
/// install one. Every check here has to tolerate media on a network share, so a
/// path that cannot be resolved or a file that cannot be read is reported as
/// "not equal" rather than thrown: the caller's fallback is to reinstall the
/// poster, which is safe, while an exception would abort the whole run.
/// </summary>
internal static class PosterPaths
{
    /// <summary>
    /// Determines whether two paths name the same file.
    /// </summary>
    /// <param name="first">First path.</param>
    /// <param name="second">Second path.</param>
    /// <returns><c>true</c> when both resolve to the same full path.</returns>
    internal static bool PathsEqual(string? first, string? second)
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

    /// <summary>
    /// Resolves a path for use as a dictionary key, leaving it as-is when it cannot be resolved.
    /// </summary>
    /// <param name="path">Path to normalize.</param>
    /// <returns>The full path when one can be formed.</returns>
    internal static string NormalizePath(string path)
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

    /// <summary>
    /// Determines whether two files hold identical bytes.
    /// </summary>
    /// <param name="first">First file path.</param>
    /// <param name="second">Second file path.</param>
    /// <returns><c>true</c> when both exist and their contents match.</returns>
    internal static bool FilesEqual(string first, string second)
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

    /// <summary>
    /// Copies a poster into place through a temporary file so a reader never sees a partial image.
    /// </summary>
    /// <param name="sourcePath">Poster to copy.</param>
    /// <param name="destinationPath">Destination path, which must not already exist.</param>
    internal static void CopyPosterAtomically(string sourcePath, string destinationPath)
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

    /// <summary>
    /// Deletes a working file, ignoring a failure to do so.
    /// </summary>
    /// <param name="path">Path to delete.</param>
    internal static void TryDelete(string path)
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
}

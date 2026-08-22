using System.Text;

namespace Jellyfin.Plugin.WitziEpisodePosters.Posters;

/// <summary>
/// Per-run diagnostics file for the poster tasks.
/// </summary>
/// <remarks>
/// The tasks write here instead of the server log because a full library pass
/// produces a line per episode, which would bury everything else Jellyfin logs.
/// Each run truncates its own file, so the log always describes the last run.
/// </remarks>
internal sealed class PosterRunLog : IDisposable
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

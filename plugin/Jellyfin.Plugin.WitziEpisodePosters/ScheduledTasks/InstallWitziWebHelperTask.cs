using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Installs the embedded Witzi pre-paint layer and browser helper into Jellyfin Web at startup.
/// </summary>
public sealed class InstallWitziWebHelperTask : IScheduledTask
{
    private const string ResourceName = "Jellyfin.Plugin.WitziEpisodePosters.Web.witzi-posters.js";
    private const string CriticalResourceName = "Jellyfin.Plugin.WitziEpisodePosters.Web.witzi-critical.css";
    private const string StartMarker = "<!-- BEGIN Witzi Theme Browser Helper -->";
    private const string EndMarker = "<!-- END Witzi Theme Browser Helper -->";
    private const string CriticalStartMarker = "<!-- BEGIN Witzi Theme Pre-Paint Layer -->";
    private const string CriticalEndMarker = "<!-- END Witzi Theme Pre-Paint Layer -->";
    private static readonly Regex HelperBlockPattern = new(
        $"{Regex.Escape(StartMarker)}[\\s\\S]*?{Regex.Escape(EndMarker)}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CriticalBlockPattern = new(
        $"{Regex.Escape(CriticalStartMarker)}[\\s\\S]*?{Regex.Escape(CriticalEndMarker)}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<InstallWitziWebHelperTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstallWitziWebHelperTask"/> class.
    /// </summary>
    public InstallWitziWebHelperTask(
        IApplicationPaths applicationPaths,
        ILogger<InstallWitziWebHelperTask> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Install Witzi web helper";

    /// <inheritdoc />
    public string Key => "InstallWitziWebHelper";

    /// <inheritdoc />
    public string Description => "Installs the embedded Witzi pre-paint layer and browser helper into Jellyfin Web for detail-ribbon layout and portrait home cards.";

    /// <inheritdoc />
    public string Category => "Startup Services";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(_applicationPaths.WebPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("Cannot install the Witzi browser helper because Jellyfin Web index was not found at {Path}.", indexPath);
                return;
            }

            var helper = await ReadEmbeddedResource(ResourceName, cancellationToken).ConfigureAwait(false);
            if (helper is null)
            {
                _logger.LogError("Cannot install the Witzi browser helper because embedded resource {ResourceName} is missing.", ResourceName);
                return;
            }

            if (helper.Contains("</script", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Cannot install the Witzi browser helper because its source contains a closing script tag.");
                return;
            }

            var critical = await ReadEmbeddedResource(CriticalResourceName, cancellationToken).ConfigureAwait(false);
            if (critical is null)
            {
                _logger.LogError("Cannot install the Witzi pre-paint layer because embedded resource {ResourceName} is missing.", CriticalResourceName);
                return;
            }

            if (critical.Contains("</style", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Cannot install the Witzi pre-paint layer because its source contains a closing style tag.");
                return;
            }

            var document = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
            var criticalInjection = $"{CriticalStartMarker}{Environment.NewLine}<style>{Environment.NewLine}{critical}{Environment.NewLine}</style>{Environment.NewLine}{CriticalEndMarker}";
            var injection = $"{StartMarker}{Environment.NewLine}<script>{Environment.NewLine}{helper}{Environment.NewLine}</script>{Environment.NewLine}{EndMarker}";

            // The pre-paint rules only do their job if the browser has them
            // before Jellyfin renders, so they belong in head rather than beside
            // the helper. User Custom CSS arrives long after a detail page has
            // already assembled itself in front of the viewer.
            var changed = TryApplyBlock(ref document, CriticalBlockPattern, criticalInjection, "</head>", indexPath);
            changed |= TryApplyBlock(ref document, HelperBlockPattern, injection, "</body>", indexPath);

            if (!changed)
            {
                _logger.LogDebug("The current Witzi pre-paint layer and browser helper are already installed in Jellyfin Web.");
                return;
            }

            await WriteIndexAtomically(indexPath, document, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Installed the Witzi pre-paint layer and browser helper into {Path}.", indexPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Jellyfin Web is not writable. Grant the Jellyfin service write access to index.html or install the helper through a compatible JavaScript injector.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not update Jellyfin Web index with the Witzi browser helper.");
        }
        finally
        {
            progress.Report(100);
        }
    }

    private static async Task<string?> ReadEmbeddedResource(string resourceName, CancellationToken cancellationToken)
    {
        await using var resource = typeof(InstallWitziWebHelperTask).Assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            return null;
        }

        using var reader = new StreamReader(resource);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd();
    }

    private bool TryApplyBlock(
        ref string document,
        Regex blockPattern,
        string injection,
        string anchorTag,
        string indexPath)
    {
        var existingBlocks = blockPattern.Matches(document);
        if (existingBlocks.Count == 1
            && string.Equals(existingBlocks[0].Value, injection, StringComparison.Ordinal))
        {
            return false;
        }

        if (existingBlocks.Count > 0)
        {
            // Replace the first block where it already lives and remove
            // accidental duplicates. Re-appending it before the anchor can
            // reorder or overwrite injections owned by other plugins.
            var replacementWritten = false;
            document = blockPattern.Replace(
                document,
                _ =>
                {
                    if (replacementWritten)
                    {
                        return string.Empty;
                    }

                    replacementWritten = true;
                    return injection;
                });

            return true;
        }

        var anchor = document.LastIndexOf(anchorTag, StringComparison.OrdinalIgnoreCase);
        if (anchor < 0)
        {
            _logger.LogWarning("Cannot install a Witzi block because {Path} has no {AnchorTag} tag.", indexPath, anchorTag);
            return false;
        }

        document = document.Insert(anchor, injection + Environment.NewLine);
        return true;
    }

    // Jellyfin Web cannot start without index.html, so it is never written in
    // place. A crash, container stop, or full disk partway through a direct
    // write would leave the web client truncated with no copy of the original
    // left to restore. Staging beside the target keeps the rename on one
    // volume so it stays atomic.
    private static async Task WriteIndexAtomically(
        string indexPath,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(indexPath)
            ?? throw new ArgumentException("The Jellyfin Web index path has no containing directory.", nameof(indexPath));
        var temporaryPath = Path.Combine(
            directory,
            "index.html.witzi-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, indexPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The staged copy is inert. Leaving it behind is better than
                // masking the original failure with a cleanup error.
            }
        }
    }
}

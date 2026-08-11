using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Installs the embedded Witzi browser helper into Jellyfin Web at startup.
/// </summary>
public sealed class InstallWitziWebHelperTask : IScheduledTask
{
    private const string ResourceName = "Jellyfin.Plugin.WitziEpisodePosters.Web.witzi-posters.js";
    private const string StartMarker = "<!-- BEGIN Witzi Theme Browser Helper -->";
    private const string EndMarker = "<!-- END Witzi Theme Browser Helper -->";
    private static readonly Regex HelperBlockPattern = new(
        $"{Regex.Escape(StartMarker)}[\\s\\S]*?{Regex.Escape(EndMarker)}",
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
    public string Description => "Installs the embedded Witzi browser helper into Jellyfin Web for detail-ribbon layout and portrait home cards.";

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

            await using var resource = typeof(InstallWitziWebHelperTask).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                _logger.LogError("Cannot install the Witzi browser helper because embedded resource {ResourceName} is missing.", ResourceName);
                return;
            }

            using var reader = new StreamReader(resource);
            var helper = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd();
            if (helper.Contains("</script", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Cannot install the Witzi browser helper because its source contains a closing script tag.");
                return;
            }

            var current = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
            var injection = $"{StartMarker}{Environment.NewLine}<script>{Environment.NewLine}{helper}{Environment.NewLine}</script>{Environment.NewLine}{EndMarker}";
            var existingBlocks = HelperBlockPattern.Matches(current);
            if (existingBlocks.Count == 1
                && string.Equals(existingBlocks[0].Value, injection, StringComparison.Ordinal))
            {
                _logger.LogDebug("The current Witzi browser helper is already installed in Jellyfin Web.");
                return;
            }

            string updated;
            if (existingBlocks.Count > 0)
            {
                // Replace the first block where it already lives and remove
                // accidental duplicates. Re-appending it before </body> can
                // reorder or overwrite injections owned by other plugins.
                var replacementWritten = false;
                updated = HelperBlockPattern.Replace(
                    current,
                    _ =>
                    {
                        if (replacementWritten)
                        {
                            return string.Empty;
                        }

                        replacementWritten = true;
                        return injection;
                    });
            }
            else
            {
                var closingBody = current.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (closingBody < 0)
                {
                    _logger.LogWarning("Cannot install the Witzi browser helper because {Path} has no closing body tag.", indexPath);
                    return;
                }

                updated = current.Insert(closingBody, injection + Environment.NewLine);
            }

            await File.WriteAllTextAsync(indexPath, updated, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Installed Witzi browser helper into {Path}.", indexPath);
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
}

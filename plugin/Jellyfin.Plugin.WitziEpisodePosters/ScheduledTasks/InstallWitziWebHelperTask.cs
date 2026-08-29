using Jellyfin.Plugin.WitziEpisodePosters.Configuration;
using Jellyfin.Plugin.WitziEpisodePosters.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.ScheduledTasks;

/// <summary>
/// Installs the embedded Witzi pre-paint layer, theme, and browser helper into Jellyfin Web at startup.
/// </summary>
public sealed class InstallWitziWebHelperTask : IScheduledTask
{
    private readonly WitziWebInstaller _installer;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstallWitziWebHelperTask"/> class.
    /// </summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    /// <param name="logger">The logger.</param>
    public InstallWitziWebHelperTask(
        IApplicationPaths applicationPaths,
        ILogger<InstallWitziWebHelperTask> logger)
    {
        _installer = new WitziWebInstaller(applicationPaths, logger);
    }

    /// <inheritdoc />
    public string Name => "Install Witzi web helper";

    /// <inheritdoc />
    public string Key => "InstallWitziWebHelper";

    /// <inheritdoc />
    public string Description => "Installs the embedded Witzi pre-paint layer, theme stylesheet, and browser helper into Jellyfin Web for detail-ribbon layout and portrait home cards.";

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
            // Jellyfin runs this at startup, which is normally after it has
            // constructed the plugin. Defaults keep the task useful rather than
            // silent if that order ever changes.
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            await _installer.InstallAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            progress.Report(100);
        }
    }
}

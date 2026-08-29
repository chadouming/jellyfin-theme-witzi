using System.Text;
using Jellyfin.Plugin.WitziEpisodePosters.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WitziEpisodePosters.Services;

/// <summary>
/// Serves the Witzi pre-paint layer, compiled theme, and browser helper with Jellyfin Web's
/// index.html by rewriting the response, without depending on a writable web folder.
/// </summary>
/// <remarks>
/// <para>
/// Writing the blocks into index.html on disk only reaches the browser where the Jellyfin service
/// account can write the web folder, and a jellyfin-web upgrade replaces the file and takes the
/// blocks with it. Where either applies, picking a palette on the configuration page changed
/// nothing the browser ever saw, and the theme had to come from the Custom CSS field instead —
/// which is exactly the post-first-paint delivery the plugin exists to replace.
/// </para>
/// <para>
/// Rewriting the response has neither limitation: it reads the plugin configuration per request,
/// so a palette applies on the next page load with no restart and nothing written anywhere. The
/// filter is additive and defensive: it only ever touches the web index response, it leaves a
/// document that already carries the current blocks untouched, and any failure serves the original
/// bytes rather than throwing into the pipeline.
/// </para>
/// </remarks>
public sealed class WitziIndexInjectionStartupFilter : IStartupFilter
{
    private readonly ILogger<WitziIndexInjectionStartupFilter> _logger;
    private int _loggedOnce;

    /// <summary>
    /// Initializes a new instance of the <see cref="WitziIndexInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public WitziIndexInjectionStartupFilter(ILogger<WitziIndexInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Registered ahead of the rest of the pipeline so this runs outermost:
            // dropping Accept-Encoding below then reliably yields an uncompressed
            // response to read and rewrite.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    // Matches the web app shell however it is requested: bare "/web", "/web/" (SPA
    // serve), and explicit "/web/index.html". EndsWith keeps this correct when
    // Jellyfin is hosted under a base-url prefix (e.g. /jellyfin/web/).
    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        // Only GET produces a body to rewrite. HEAD and the rest pass straight
        // through so the host emits its own headers; buffering them would compute a
        // Content-Length against an empty downstream body.
        if (!IsIndexRequest(context.Request.Path.Value)
            || !HttpMethods.IsGet(context.Request.Method))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || configuration.DisableIndexMiddleware)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        // Normalize the request so the static handler returns a complete, plain-text
        // 200 to rewrite: drop Accept-Encoding (no compression) and Range/If-Range (a
        // 206 would otherwise pass through un-injected with a wrong total length).
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        catch
        {
            // A downstream failure is not this filter's to swallow. Discard the
            // partially buffered body — it never reached the real stream — and
            // rethrow: the response has not started, so the host's exception handler
            // can still render a clean error page. Flushing the partial buffer here
            // would commit a truncated, 200-looking response.
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == 200
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isHtml)
        {
            // 304, redirects, non-HTML: pass straight through unchanged.
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string document;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            document = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        var changed = false;
        try
        {
            var assets = await WitziWebAssets
                .LoadAsync(configuration, _logger, context.RequestAborted)
                .ConfigureAwait(false);

            if (assets is not null)
            {
                changed = WitziIndexDocument.TryApply(ref document, assets, _logger, "the Jellyfin Web index response");

                if (changed && Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation("Serving the Witzi web assets with Jellyfin Web's index.html.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never break index.html. Serving Jellyfin unthemed is recoverable;
            // serving nothing is not.
            _logger.LogWarning(ex, "Could not add the Witzi web assets to the Jellyfin Web index response, so it is served unchanged.");
            changed = false;
        }

        if (!changed)
        {
            // The document already carries the current blocks, most likely written to
            // disk by the startup task. Passing the original bytes through keeps the
            // static handler's ETag and Last-Modified valid.
            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(document);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        // The body changed, so validators the static-file handler set no longer
        // describe it, and range requests are not supported on the rewritten
        // document (Range is already stripped on the way in).
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}

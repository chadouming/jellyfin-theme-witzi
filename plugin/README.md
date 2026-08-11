# Witzi Episode Posters plugin

This Jellyfin 12 plugin creates a real 2:3 Primary image for episodes that only
have Jellyfin's landscape Screen Grabber image. It uses Jellyfin's configured
FFmpeg media encoder to sample frames at 18%, 50%, and 82%, then composes them
locally into a Catppuccin Mocha/Witzi portrait poster. Frame extraction asks
FFmpeg to select an available hardware decoder automatically and falls back to
Jellyfin's regular software extraction if the GPU path cannot be used. Every
new poster receives three distinct, randomly selected frame-border colors from
the Witzi/Catppuccin palette.

The output is `<episode video basename>.jpg` beside the video. That filename is
recognized as `ImageType.Primary` by Jellyfin 12's `EpisodeLocalImageProvider`,
so native clients can use the image without the Witzi browser helper.

Before starting FFmpeg, the plugin checks Jellyfin's registered Primary image
and every episode-specific sidecar name supported by Jellyfin's local image
provider. This includes the episode basename and `-thumb` variants, all
supported image extensions, and both the media directory and its `metadata`
subdirectory. Existing posters are never regenerated or overwritten; Primary
images whose dimensions cannot be determined are preserved conservatively.
Media folders must be writable by the Jellyfin server account.

After installation and a server restart, run **Dashboard -> Scheduled Tasks ->
Library -> Generate Witzi episode posters**. The plugin does not create an
automatic trigger; run the task manually whenever new episodes need posters.
Existing poster sidecars are never overwritten, so their colors remain stable.

At server startup, the plugin also installs its embedded `witzi-posters.js`
helper into Jellyfin Web. This makes the overview and item details live inside
the detail ribbon and enables portrait artwork on supported home rows without
depending on the JavaScript Injector plugin. JavaScript is required because CSS
cannot move live DOM nodes, replace episode image requests, or coordinate
cached backdrop transitions, and the separate injector targets Jellyfin 10.11
rather than this plugin's Jellyfin 12 ABI. The Jellyfin service needs write
access to the web client's `index.html`; if the web directory is read-only, the
startup task logs an error and the helper can still be installed manually.

The project targets Jellyfin ABI 12.0.0.0, .NET 10, and the Jellyfin 12.0 RC4
packages. It is not loadable by Jellyfin 10.x.

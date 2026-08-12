# Witzi Episode Posters plugin

This Jellyfin 12 plugin creates a dedicated 2:3 Witzi poster for every episode
that does not already have one. It uses Jellyfin's configured FFmpeg media
encoder to sample frames at 18%, 50%, and 82%, then composes them locally into
a Catppuccin Mocha/Witzi portrait poster. Frame extraction asks FFmpeg to
select an available hardware decoder automatically and falls back to
Jellyfin's regular software extraction if the GPU path cannot be used. Every
new poster receives three distinct, randomly selected frame-border colors from
the Witzi/Catppuccin palette. The scheduled task uses Jellyfin's **Parallel
image encoding limit** as its maximum number of concurrent episode workers. If
that setting is empty, it follows Jellyfin's core-count-based default.

The output is `<episode video basename>-witzi.jpg` beside the video and is
registered as the episode's `ImageType.Primary`, so native clients can use it
without the Witzi browser helper. A different previous Primary image is not
deleted or overwritten; only Jellyfin's active Primary registration changes.

Before starting FFmpeg, the plugin checks for the dedicated Witzi file in both
the media directory and its `metadata` subdirectory. If it is already Primary,
the episode is skipped; if it exists but another image became Primary, the
existing file is registered again without regenerating it. The plugin also
recognizes a registered 1000 x 1500 `<episode video basename>.jpg` created by
versions through 0.1.10. Reserved Witzi files are never overwritten. Media
folders must be writable by the Jellyfin server account.

After installation and a server restart, run **Dashboard -> Scheduled Tasks ->
Library -> Generate Witzi episode posters**. The plugin does not create an
automatic trigger; run the task manually whenever new episodes need posters.
Existing Witzi sidecars are never overwritten, so their colors remain stable.

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

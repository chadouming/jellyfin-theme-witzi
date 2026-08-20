# Witzi Episode Posters plugin

This Jellyfin 12 plugin creates a dedicated 2:3 Witzi poster for every episode
that does not already have one. It uses Jellyfin's configured FFmpeg media
encoder to sample frames at 18%, 50%, and 82%, then composes them locally into
a Catppuccin Mocha/Witzi portrait poster. Frame extraction asks FFmpeg to
select an available hardware decoder automatically and falls back to
Jellyfin's regular software extraction if the GPU path cannot be used. Every
new poster receives three distinct, randomly selected frame-border colors from
the Witzi/Catppuccin palette. The scheduled task always uses at most four
concurrent episode workers, regardless of Jellyfin's **Parallel image encoding
limit**, to avoid exhausting memory or process threads during large AV1 runs.

Each run collects the full episode-id list once and then works through it, so
registering artwork cannot reorder rows out from under a partially completed
pass. Episodes that share one multi-episode video file also share one poster,
which is built once and registered for each of them.

The reusable output is `<episode video basename>-witzi.jpg` beside the video.
The task also installs an identical copy as `<episode video basename>.jpg`, the
episode Primary filename Jellyfin recognizes after later metadata refreshes.
Native clients can therefore use it without the Witzi browser helper. Existing
local Primary sidecars are moved to ignored `*-witzi-original*` backup names;
remote or provider-managed source artwork is not deleted.

The plugin also registers the Witzi poster as a local image provider, so
Jellyfin asks for it during every metadata refresh instead of only when the
poster is first installed. A library scan rebuilds each image choice from what
the providers return, so a poster that is merely written to disk can be replaced
by a downloaded episode screenshot. The provider is offered ahead of Jellyfin's
own episode provider and reports a file stored beside the media, which also
marks the Primary image as locally provided and suppresses the remote fetch.
Local image providers cannot be turned off in a library's image fetcher
settings, so this applies to every library.

Before starting FFmpeg, the plugin checks for the dedicated Witzi file in both
the media directory and its `metadata` subdirectory. If it is already Primary,
the episode is skipped; if it exists but another image became Primary, the
existing file is installed again without regenerating it. The plugin also
recognizes a registered 1000 x 1500 `<episode video basename>.jpg` created by
versions through 0.1.10. Reserved Witzi files are never overwritten. Media
folders must be writable by the Jellyfin server account.

After installation and a server restart, run **Dashboard -> Scheduled Tasks ->
Library -> Generate Witzi episode posters**. The plugin does not create an
automatic trigger; run the task manually whenever new episodes need posters.
Existing Witzi sidecars are never overwritten, so their colors remain stable.
Each run replaces `witzi-episode-posters.log` in Jellyfin's configured log
directory. It contains the per-episode generated, reused, skipped, and failed
results plus a final summary, instead of sending that detail to the main log.

At server startup, the plugin installs two embedded blocks into Jellyfin Web.
A small pre-paint stylesheet goes into `<head>`, and the `witzi-posters.js`
helper goes before `</body>`.

The pre-paint layer exists because a detail page used to assemble itself in
front of the viewer: Jellyfin renders its own layout, then the helper moves the
title, buttons, overview, and metadata into the ribbon. User Custom CSS loads
far too late to cover that, but a plugin can write into `<head>`, so the moved
regions stay hidden until the helper reports the panel is composed. A CSS
failsafe reveals them regardless after 600ms, so a stalled helper can never
leave a page blank. Both blocks are refreshed in place on later startups and
never relocated past injections owned by other plugins.

The helper This makes the overview and item details live inside
the detail ribbon and enables portrait artwork on supported home rows without
depending on the JavaScript Injector plugin. JavaScript is required because CSS
cannot move live DOM nodes, replace episode image requests, or coordinate
cached backdrop transitions, and the separate injector targets Jellyfin 10.11
rather than this plugin's Jellyfin 12 ABI. The Jellyfin service needs write
access to the web client's `index.html`. The replacement is staged beside the
target and renamed where the directory allows new files, because that cannot
leave a truncated web client behind if the write is interrupted. Where the
directory refuses new files, which several container images do, the task warns
and rewrites `index.html` in place instead. If `index.html` itself is not
writable, the startup task logs an error and the helper can still be installed
manually.

The project targets Jellyfin ABI 12.0.0.0, .NET 10, and the Jellyfin 12.0 RC5
packages. It is not loadable by Jellyfin 10.x.

Once a poster is written, open browsers pick it up without a reload. The helper
subscribes to the websocket messages Jellyfin already sends and, on a
`LibraryChanged` or `UserDataChanged` message, drops only the affected items
from its poster cache and rescans. It appends to the client's own listener list
and never takes over the socket, so a client that exposes neither still
refreshes on the cache timeout.

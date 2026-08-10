# Witzi Episode Posters plugin

This Jellyfin 12 plugin creates a real 2:3 Primary image for episodes that only
have Jellyfin's landscape Screen Grabber image. It uses Jellyfin's configured
media encoder to sample frames at 18%, 50%, and 82%, then composes them locally
into a Catppuccin Mocha/Witzi portrait poster.

The output is `<episode video basename>.jpg` beside the video. That filename is
recognized as `ImageType.Primary` by Jellyfin 12's `EpisodeLocalImageProvider`,
so native clients can use the image without the Witzi browser helper.

The plugin does not overwrite an existing image sidecar. It also leaves an
existing portrait Primary image alone. Media folders must be writable by the
Jellyfin server account.

After installation and a server restart, run **Dashboard -> Scheduled Tasks ->
Library -> Generate Witzi episode posters**. The plugin does not create an
automatic trigger; run the task manually whenever new episodes need posters.

The project targets Jellyfin ABI 12.0.0.0, .NET 10, and the Jellyfin 12.0 RC4
packages. It is not loadable by Jellyfin 10.x.

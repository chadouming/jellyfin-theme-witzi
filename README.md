# Witzi for Jellyfin

A Jellyfin Web theme family built from Witzi's visual system: softly patterned canvases, rounded playing-card surfaces, inset card grooves, compact 8px controls, vivid two-color accents, and clear tactile interaction states.

**[Preview the themes and copy an install link →](https://chadouming.github.io/jellyfin-theme-witzi/)**

The default is Catppuccin Mocha. Six matching variants are included:

| Variant | Character | File |
| --- | --- | --- |
| Mocha | Soft purple and blue on deep navy | `dist/witzi-mocha.css` |
| Latte | Bright, readable light theme | `dist/witzi-latte.css` |
| Nord | Cool frost blues | `dist/witzi-nord.css` |
| Solarized | Inky teal with blue accents | `dist/witzi-solarized.css` |
| Dracula | Gothic violet and pink | `dist/witzi-dracula.css` |
| Gruvbox | Warm retro gold and orange | `dist/witzi-gruvbox.css` |

Desktop detail pages use a fixed, viewport-aware artwork rail: the prominent poster begins with a small breathing space below the top menu and matches the episode-frame width. Series scale the portrait poster and enlarged landscape Next Up card together to use the available viewport height while preserving both aspect ratios. Playable movies use a larger height-aware poster rail, with full-width Video, Audio, and Subtitles selectors stacked below the poster so their labels and selected titles remain readable. The solid, compact information ribbon shares the poster's exact top edge and contains the title, action buttons, overview, Show More control, and a vertically stacked item metadata group in one clipped, full-width panel; Series and Season lists start directly below it, while remaining movie information follows in the same scrolling column. Detail logos, keyword tags, and external database links are hidden.

## Install

There are two delivery paths. The plugin is the better one: Jellyfin renders the
Custom CSS field from a React component that waits on the branding request, so a
theme pasted there cannot reach the browser until after the first paint. The page
shows stock Jellyfin colours and landscape home rows, then snaps to Witzi. Serving
the theme from `index.html` removes that flash entirely.

### From the plugin (recommended)

Install **Witzi Episode Posters** (see [below](#jellyfin-12-episode-poster-plugin)),
then open **Dashboard → Plugins → Witzi Episode Posters**, choose a palette, and
save. The plugin writes the compiled bundle into Jellyfin Web's `index.html`
alongside the pre-paint layer and the browser helper, and rewrites it whenever you
change the palette. Nothing goes in the Custom CSS field.

The bundle is written at the end of `<body>`, the only place it outranks the
palette Jellyfin Web installs at runtime, so it also outranks the Custom CSS
field: overrides pasted there need `!important` while the plugin is serving the
theme.

Turn **Serve the theme from Jellyfin Web** off on that page to fall back to Custom
CSS delivery, or to run a hand-edited copy of the theme.

### From the Custom CSS field

Open **Dashboard → General → Custom CSS code**, paste one line, and save. Mocha is
the default:

```css
@import url("https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-mocha.css");
```

Use one of these GitHub Pages imports to follow the latest published version:

```css
/* Catppuccin Mocha */
@import url("https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-mocha.css");

/* Catppuccin Latte */
@import url("https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-latte.css");

/* Nord */
@import url("https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-nord.css");

/* Solarized Dark */
@import url("https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-solarized.css");

/* Dracula */
@import url("https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-dracula.css");

/* Gruvbox Dark */
@import url("https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-gruvbox.css");
```

Use Jellyfin's built-in **Dark** theme under your user display settings for every variant except Latte; use **Light** for Latte so any unstyled fallback controls match.

For an installation without an external stylesheet request, download a compiled CSS file from the [v1.1.25 release](https://github.com/chadouming/jellyfin-theme-witzi/releases/tag/v1.1.25) and paste its full contents into the Custom CSS field. The compiled files are standalone and contain their SVG pattern as an embedded data URI.

For a version-pinned CDN import, use `https://cdn.jsdelivr.net/gh/chadouming/jellyfin-theme-witzi@v1.1.25/dist/witzi-mocha.css` and change the filename for another palette. Version-pinned links only change when you deliberately select a newer release.

Clients can disable server-provided custom CSS in their display preferences. As of Jellyfin 10.11, server custom CSS is intentionally not loaded in the administration dashboard; the rest of Jellyfin Web remains themed. See Jellyfin's [upstream explanation](https://github.com/jellyfin/jellyfin-web/issues/7220#issuecomment-3428862571).

Both limitations apply to the Custom CSS path only. A theme served from the plugin
reaches every client and the dashboard, because it is part of the page rather than
something the application fetches and injects after it starts.

### Poster cards for Continue Watching and Next Up

Jellyfin supplies those two rows as landscape cards, so CSS alone cannot ask the server for a different image. Witzi now has two cooperating pieces:

- the **Witzi Episode Posters** server plugin creates a persistent 2:3 Primary image from each episode's own video; and
- the [`dist/witzi-posters.js`](dist/witzi-posters.js) browser helper makes Jellyfin Web request portrait Primary images for Continue Watching and Next Up, maintains seamless backdrop transitions, and moves live detail content into the ribbon.

The browser helper selects:

- an episode's own portrait Primary poster, without substituting series, season, or parent artwork;
- the item's own portrait Primary poster for a movie; or
- Jellyfin's native artwork, contained without cropping, when the item's own Primary fails to load.

The rows always use the same portrait geometry as Recently Added. The helper detects episodes from both API metadata and Jellyfin's card markup, verifies each candidate in the browser before swapping artwork, retries incomplete poster metadata, and rejects landscape Primary images. Native artwork stays visible until a poster has loaded, so failed lookups never produce empty cards.

On desktop detail pages, the same helper moves Jellyfin's live title/info, action buttons, `detailSectionContent`, and `itemDetailsGroup` nodes directly into the real ribbon as the view is created. Every part therefore shares one width, one background, and one clipping edge while retaining Jellyfin's live overview expansion and metadata updates. The synchronizer relocates the original nodes instead of copying them, replaces stale nodes when Jellyfin recreates the detail view, and processes every cached detail-page instance. Poster and ribbon alignment comes from shared layout variables plus one initial rendered-edge adjustment when the ribbon is composed; scrolling never triggers further geometry reads or style corrections. The combined panel requires the JavaScript helper; without it, Jellyfin keeps its native detail-page structure.

The plugin injects this bundled JavaScript because Custom CSS can only style Jellyfin's existing elements: it cannot move live DOM nodes, request replacement Primary images, or coordinate cached backdrop transitions. The helper waits for the theme before it starts, so serving the theme from the plugin also gets it working from the first paint instead of after Jellyfin's branding request resolves. The commonly used JavaScript Injector plugin currently targets Jellyfin 10.11, so it is not a compatible delivery mechanism for this Jellyfin 12 ABI build. Installing the helper from Witzi Episode Posters keeps the required CSS and JavaScript paired at matching versions and makes the behavior available after a normal Jellyfin restart.

#### Jellyfin 12 episode-poster plugin

Add this URL under **Dashboard -> Plugins -> Repositories**:

```text
https://chadouming.github.io/jellyfin-theme-witzi/manifest.json
```

Install **Witzi Episode Posters**, restart Jellyfin, then run **Dashboard -> Scheduled Tasks -> Library -> Generate Witzi episode posters** once. The task:

- uses Jellyfin's configured FFmpeg/media encoder at 18%, 50%, and 82% of each episode;
- automatically uses a supported GPU decoder when available, with a software fallback;
- uses at most four concurrent episode workers regardless of Jellyfin's **Parallel image encoding limit**, avoiding memory and process-thread exhaustion during large AV1 runs;
- builds a 1000 x 1500 Witzi-styled frame collage;
- chooses three distinct border colors randomly from the Witzi palette for each new poster;
- writes a reusable `<episode video basename>-witzi.jpg`, installs an identical `<episode video basename>.jpg` that remains Primary after metadata refreshes, and preserves conflicting local sidecars under ignored `*-witzi-original*` backup names;
- registers the new image immediately and reports its 2:3 dimensions; and
- skips episodes whose Witzi poster is already Primary, and reinstalls an existing dedicated Witzi file without running FFmpeg if another image became Primary.

Each run replaces `witzi-episode-posters.log` in Jellyfin's configured log directory. That dedicated file records every generated, reused, skipped, and failed episode plus a final outcome summary, keeping per-episode FFmpeg and poster diagnostics out of Jellyfin's main log.

The Jellyfin service account therefore needs write access to the media folders. Poster generation has no automatic trigger; run the Library task manually whenever new episodes are added. GPU decoding depends on the FFmpeg build, exposed device, driver, and source codec; unsupported combinations fall back automatically. Dedicated Witzi sidecars are not recolored or overwritten, and a registered 1000 x 1500 legacy poster produced by plugin 0.1.10 or earlier is recognized without regeneration. Starting with plugin 0.1.5.0, its startup task also installs the embedded browser helper into Jellyfin Web, removing the Jellyfin 12 dependency on JavaScript Injector; version 0.1.6.0 waits for Jellyfin to load the user's Custom CSS before activating that helper, 0.1.7.0 restores normal detail scrolling, 0.1.8.0 precisely aligns each new ribbon to its poster once, 0.1.9.0 generates only for episodes while preserving existing registered or sidecar posters before FFmpeg starts, 0.1.10.0 scopes browser-helper updates to affected Jellyfin elements while preserving the position of other plugin injections, 0.1.11.0 uses dedicated reusable Witzi sidecars plus Jellyfin's Parallel image encoding limit, 0.1.12.0 installs the reusable artwork under Jellyfin's persistent episode Primary filename while retaining conflicting local artwork as backups, 0.1.13.0 writes detailed generation diagnostics and outcome counts to its own log file, 0.1.14.0 caps generation at four concurrent episode workers to prevent resource exhaustion, and 0.1.15.0 walks one unpaged episode-id snapshot so registering artwork can no longer shift an episode past an offset boundary unprocessed, while episodes that share a multi-episode video file build their poster once instead of racing for it. Version 1.1.17.0 adopts the theme version so both ship as one release, adds a pre-paint stylesheet injected into `<head>` so a detail page no longer assembles itself in front of the viewer, and rewrites `index.html` through an atomic rename instead of in place. Version 1.1.18.0 builds against the Jellyfin 12.0 RC5 packages and refreshes regenerated posters in open browsers without a reload, and 1.1.19.0 restores helper installation on servers whose Jellyfin Web directory does not allow new files. Version 1.1.20.0 registers the poster as a local image provider so a library scan can no longer replace it with Jellyfin's default episode artwork, and 1.1.21.0 adds a post-scan pass that restores any poster a scan still replaced, recording it in `witzi-episode-posters-scan.log`. Version 1.1.22.0 serializes the repository write so concurrent episode saves no longer violate a unique index on PostgreSQL, and 1.1.23.0 treats a write lost to Jellyfin saving the same episode as a deferred registration rather than a failed poster, since the image provider and post-scan pass select it afterwards. Version 1.1.24.0 embeds the six compiled palettes and serves the selected one from `index.html`, adding a plugin configuration page to pick it, so the theme is in place before Jellyfin's first paint instead of arriving with the branding request, and 1.1.25.0 moves that bundle from `<head>` to the end of `<body>`. Jellyfin Web installs its own palette after anything `<head>` already carries -- MUI writes a `:root` block of `--jf-palette-*` values into `<head>` as the bundle boots, and `themes/<id>/theme.css` arrives as a `<link>` React renders inside `#reactRoot` -- so the theme's `:root` bridge tied with both on specificity and lost every tie, leaving the page looking untouched. Nothing Jellyfin renders comes after the end of `<body>`, and its bundles load with `defer`, so the theme still applies before the first paint; it now also outranks the Custom CSS field, so overrides pasted there need `!important`. The service account must be able to update Jellyfin Web's `index.html`. The compiled plugin targets **Jellyfin ABI 12.0.0.0**, **.NET 10**, and the current official **Jellyfin 12.0 RC5** packages; it will not load on Jellyfin 10.x. The [manual plugin ZIP](https://github.com/chadouming/jellyfin-theme-witzi/releases/download/v1.1.25/Witzi.Episode.Posters_1.1.25.0.zip) is also available from the release.

For Jellyfin 10.11, install the third-party [JavaScript Injector plugin](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector), create an enabled script entry, and paste the contents of `dist/witzi-posters.js`. To follow the GitHub Pages copy automatically, the script entry can instead load it:

```js
(function () {
  const script = document.createElement('script');
  script.src = `https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-posters.js?v=${Date.now()}`;
  document.head.appendChild(script);
}());
```

Pasting the full file keeps the code local and pinned, so it must be replaced after helper updates. The hosted loader cache-busts on each page load and follows future updates automatically. The helper changes card artwork only in Jellyfin Web surfaces where JavaScript injection is active; native clients keep their own layout.

If a literal such as `src=../Javascript/...` is visible in a media row, remove the malformed injector entry that contains it. The Custom CSS box must contain only the CSS `@import` line, and the JavaScript Injector entry must contain raw JavaScript like the block above—never an HTML `<script src="...">` tag or a bare `src=...` attribute. Neither valid Witzi file renders text into Recently Added.

If an episode frame is still visible, run `document.documentElement.dataset.witziPosters` in the browser console. It returns `"active"` when the current helper is running; any other result means the JavaScript Injector entry is missing, disabled, or still contains an older pasted copy.

### Backdrops

Enable **Settings → Display → Backdrops** for each Jellyfin Web client where you want dynamic artwork. Witzi makes the page canvas transparent while an internal or wrapper-provided backdrop is active. Internal Jellyfin artwork receives Witzi's palette-colored base, soft `2.5px` blur, gentle desaturation, accent glow, and slight scale-up; backdrop-less pages continue to use the Witzi pattern.

When the browser helper is installed, it keeps the last successfully loaded backdrop in an independent two-layer cache while Jellyfin preloads the next image. Only a loaded image can enter the crossfade, and stale requests are ignored, so Jellyfin clearing its native backdrop container cannot expose the solid theme color between images. The cache and page canvas are suppressed automatically while video media is playing.

## Compatibility

The theme targets Jellyfin Web 10.11 and the current palette-variable model in Jellyfin Web 12. It maps the official `--jf-palette-*` variables for current components and also styles stable legacy selectors used by media cards, details, playback, Live TV, forms, tabs, dialogs, and navigation.

Native clients that do not embed Jellyfin Web will not load server custom CSS. Web-wrapper clients may expose only the selectors supported by their bundled Jellyfin Web version.

## Development

Source files stay intentionally dependency-free:

```text
assets/          Witzi's palette-specific SVG tiles
src/             Shared styling
src/palettes/    Palette tokens
themes/          Small composable @import entry points
dist/            Standalone CSS builds and the browser helper
scripts/         Dependency-free build/check script
plugin/          Jellyfin 12 episode-poster plugin source and ABI manifest
  Jellyfin.Plugin.WitziEpisodePosters/
    Posters/     Poster identity, composition, and installation
    Providers/   Supplies the poster to Jellyfin as a local image
    ScheduledTasks/
    Web/         Pre-paint layer and poster-helper source, embedded into the
                 plugin assembly and also inlined into the theme bundle
```

After changing a palette or the shared layer:

```bash
npm run build
npm test
```

Do not edit `dist/` directly; it is regenerated from `src/` and the plugin's `Web/`.

## Design notes

- Artwork remains the focal point. Pattern tiles stay on empty page canvases and fall away over backdrops.
- Witzi's card metaphor becomes a framed media-card surface with an inset groove and small diamond marker.
- Focus rings, reduced-motion behavior, responsive sizing, and readable accent foregrounds are included.
- The six palettes and their SVG motifs are adapted directly from Witzi's frontend theme definitions in `C:\witzi-monolitic`.
- Witzi's two McDonald's-branded palettes are not redistributed here because their source uses official brand artwork without a repository license covering that asset.

Palette projects and licenses are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

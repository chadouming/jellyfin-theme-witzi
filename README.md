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

Desktop detail pages use a fixed, viewport-aware artwork rail: the enlarged media logo sits above a prominent poster matched to the episode-frame width. Series pages place one enlarged landscape Next Up card below it, while movies use that lower rail slot for stacked Video, Audio, and Subtitles selectors. The information ribbon begins at the top content edge and contains the title, action buttons, overview, Show More control, and complete item metadata group; Series and Season lists start directly below it, while remaining movie information follows in the same scrolling column. Keyword tags and external database links are hidden.

## Install

Open **Dashboard → General → Custom CSS code**, paste one line, and save. Mocha is the default:

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

For an installation without an external stylesheet request, download a compiled CSS file from the [v1.1.5 release](https://github.com/chadouming/jellyfin-theme-witzi/releases/tag/v1.1.5) and paste its full contents into the Custom CSS field. The compiled files are standalone and contain their SVG pattern as an embedded data URI.

For a version-pinned CDN import, use `https://cdn.jsdelivr.net/gh/chadouming/jellyfin-theme-witzi@v1.1.5/dist/witzi-mocha.css` and change the filename for another palette. Version-pinned links only change when you deliberately select a newer release.

Clients can disable server-provided custom CSS in their display preferences. As of Jellyfin 10.11, server custom CSS is intentionally not loaded in the administration dashboard; the rest of Jellyfin Web remains themed. See Jellyfin's [upstream explanation](https://github.com/jellyfin/jellyfin-web/issues/7220#issuecomment-3428862571).

### Poster cards for Continue Watching and Next Up

Jellyfin supplies those two rows as landscape cards, so CSS alone cannot ask the server for a different image. Witzi now has two cooperating pieces:

- the **Witzi Episode Posters** server plugin creates a persistent 2:3 Primary image from each episode's own video; and
- the optional [`dist/witzi-posters.js`](dist/witzi-posters.js) browser helper makes Jellyfin Web request portrait Primary images for Continue Watching and Next Up, and maintains seamless backdrop transitions.

The browser helper selects:

- an episode's own portrait Primary poster, then the series poster, then its season/parent poster;
- the item's own portrait Primary poster for a movie; or
- Jellyfin's native artwork, contained without cropping, only when every poster candidate fails to load.

The rows always use the same portrait geometry as Recently Added. The helper detects episodes from both API metadata and Jellyfin's card markup, verifies each candidate in the browser before swapping artwork, retries incomplete poster metadata, and rejects landscape Primary images. Native artwork stays visible until a poster has loaded, so failed lookups never produce empty cards.

On desktop detail pages, the same helper moves Jellyfin's live `detailSectionContent` and `itemDetailsGroup` containers into the detail ribbon. This carries the overview and Show More control together, and because the helper relocates the original containers instead of copying them, Jellyfin's expansion behavior and metadata updates remain active. The synchronizer also replaces stale ribbon nodes when Jellyfin recreates the detail view and processes every cached detail-page instance. If JavaScript Injector does not load the helper, the CSS joins the original containers to the ribbon as one continuous surface above the season list, preserving the requested layout without moving the live nodes.

#### Jellyfin 12 episode-poster plugin

Add this URL under **Dashboard -> Plugins -> Repositories**:

```text
https://chadouming.github.io/jellyfin-theme-witzi/manifest.json
```

Install **Witzi Episode Posters**, restart Jellyfin, then run **Dashboard -> Scheduled Tasks -> Library -> Generate Witzi episode posters** once. The task:

- uses Jellyfin's configured FFmpeg/media encoder at 18%, 50%, and 82% of each episode;
- automatically uses a supported GPU decoder when available, with a software fallback;
- builds a 1000 x 1500 Witzi-styled frame collage;
- chooses three distinct border colors randomly from the Witzi palette for each new poster;
- writes `<episode video basename>.jpg` beside the media, which Jellyfin 12 recognizes as the episode's Primary image;
- registers the new image immediately and reports its 2:3 dimensions; and
- never overwrites an existing image sidecar or an existing portrait Primary image.

The Jellyfin service account therefore needs write access to the media folders. Poster generation has no automatic trigger; run the Library task manually whenever new episodes are added. GPU decoding depends on the FFmpeg build, exposed device, driver, and source codec; unsupported combinations fall back automatically. Existing sidecars are not recolored or overwritten. The compiled plugin targets **Jellyfin ABI 12.0.0.0**, **.NET 10**, and the current official **Jellyfin 12.0 RC4** packages; it will not load on Jellyfin 10.x. The [manual plugin ZIP](https://github.com/chadouming/jellyfin-theme-witzi/releases/download/v1.1.5/Witzi.Episode.Posters_0.1.4.0.zip) is also available from the release.

To enable it on Jellyfin 10.11, install the third-party [JavaScript Injector plugin](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector), create an enabled script entry, and paste the contents of `dist/witzi-posters.js`. To follow the GitHub Pages copy automatically, the script entry can instead load it:

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
plugin/          Jellyfin 12 episode-poster plugin source and ABI manifest
src/palettes/    Palette tokens
src/             Shared styling and optional poster-helper source
themes/          Small composable @import entry points
dist/            Standalone CSS builds and the browser helper
scripts/         Dependency-free build/check script
```

After changing a palette or the shared layer:

```bash
npm run build
npm test
```

Do not edit `dist/` directly; it is regenerated from `src/`.

## Design notes

- Artwork remains the focal point. Pattern tiles stay on empty page canvases and fall away over backdrops.
- Witzi's card metaphor becomes a framed media-card surface with an inset groove and small diamond marker.
- Focus rings, reduced-motion behavior, responsive sizing, and readable accent foregrounds are included.
- The six palettes and their SVG motifs are adapted directly from Witzi's frontend theme definitions in `C:\witzi-monolitic`.
- Witzi's two McDonald's-branded palettes are not redistributed here because their source uses official brand artwork without a repository license covering that asset.

Palette projects and licenses are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

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

For an installation without an external stylesheet request, download a compiled CSS file from the [v1.0.0 release](https://github.com/chadouming/jellyfin-theme-witzi/releases/tag/v1.0.0) and paste its full contents into the Custom CSS field. The compiled files are standalone and contain their SVG pattern as an embedded data URI.

For a version-pinned CDN import, use `https://cdn.jsdelivr.net/gh/chadouming/jellyfin-theme-witzi@v1.0.0/dist/witzi-mocha.css` and change the filename for another palette. Version-pinned links only change when you deliberately select a newer release.

Clients can disable server-provided custom CSS in their display preferences. As of Jellyfin 10.11, server custom CSS is intentionally not loaded in the administration dashboard; the rest of Jellyfin Web remains themed. See Jellyfin's [upstream explanation](https://github.com/jellyfin/jellyfin-web/issues/7220#issuecomment-3428862571).

### Poster cards for Continue Watching and Next Up

Jellyfin supplies those two rows as landscape cards, so CSS alone cannot ask the server for a different image. The optional [`dist/witzi-posters.js`](dist/witzi-posters.js) helper uses Jellyfin's already-authenticated browser API client to select:

- the season or series Primary poster for an episode;
- the item's own Primary poster for a movie; or
- the native landscape artwork, contained without cropping, when no poster exists or the lookup fails.

The rows always use the same portrait geometry as Recently Added. When the helper is not installed or no poster exists, Jellyfin's native landscape artwork is contained inside that portrait frame instead of being cropped.

To enable it on Jellyfin 10.11, install the third-party [JavaScript Injector plugin](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector), create an enabled script entry, and paste the contents of `dist/witzi-posters.js`. To follow the GitHub Pages copy automatically, the script entry can instead load it:

```js
(function () {
  const script = document.createElement('script');
  script.src = 'https://chadouming.github.io/jellyfin-theme-witzi/dist/witzi-posters.js';
  document.head.appendChild(script);
}());
```

Pasting the full file keeps the code local and pinned. Using the hosted loader follows future updates after a browser refresh. The helper changes card artwork only in Jellyfin Web surfaces where JavaScript injection is active; native clients keep their own layout.

### Backdrops

Enable **Settings → Display → Backdrops** for each Jellyfin Web client where you want dynamic artwork. Witzi leaves Jellyfin's `.backdropImage` layer intact, adds a readable palette tint above it, and respects `backgroundContainer-transparent` when a wrapper client renders its own backdrop. Backdrop-less pages continue to use the Witzi pattern.

## Compatibility

The theme targets Jellyfin Web 10.11 and the current palette-variable model in Jellyfin Web 12. It maps the official `--jf-palette-*` variables for current components and also styles stable legacy selectors used by media cards, details, playback, Live TV, forms, tabs, dialogs, and navigation.

Native clients that do not embed Jellyfin Web will not load server custom CSS. Web-wrapper clients may expose only the selectors supported by their bundled Jellyfin Web version.

## Development

Source files stay intentionally dependency-free:

```text
assets/          Witzi's palette-specific SVG tiles
src/palettes/    Palette tokens
src/             Shared styling and optional poster-helper source
themes/          Small composable @import entry points
dist/            Standalone CSS builds and the poster helper
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

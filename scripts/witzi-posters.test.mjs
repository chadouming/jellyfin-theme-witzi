import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

class ClassList {
  values = new Set();

  add(value) { this.values.add(value); }
  remove(value) { this.values.delete(value); }
  contains(value) { return this.values.has(value); }
}

function unrefTimeout(callback, delay) {
  const timer = setTimeout(callback, delay);
  timer.unref?.();
  return timer;
}

function createCard(id, type) {
  const attributes = new Map([
    ['data-src', `https://jellyfin.test/Items/${id}/Images/Backdrop`]
  ]);
  const image = {
    style: { backgroundImage: `url("https://jellyfin.test/Items/${id}/Images/Backdrop")` },
    setAttribute(name, value) { attributes.set(name, value); },
    getAttribute(name) { return attributes.get(name); },
    removeAttribute(name) { attributes.delete(name); },
    querySelector() { return null; }
  };

  return {
    dataset: { id, type },
    classList: new ClassList(),
    image,
    querySelector(selector) {
      return selector === '.cardImageContainer' ? image : null;
    }
  };
}

test('uses loadable posters and retains native artwork when candidates fail', async () => {
  const cards = [
    createCard('episode-inherited', 'Episode'),
    createCard('episode-parent-fetch', 'Episode'),
    createCard('episode-type-hint', 'Episode'),
    createCard('movie', 'Movie'),
    createCard('movie-landscape-primary', 'Movie'),
    createCard('episode-no-poster', 'Episode')
  ];
  const calls = [];
  const imageRequests = [];
  let observerCallback;
  let helperStatus;

  const api = {
    getCurrentUserId: () => 'user-1',
    getScaledImageUrl: (id, options) => `https://jellyfin.test/Items/${id}/Images/${options.type}?tag=${options.tag}`,
    async getItems(_userId, options) {
      calls.push(options.Ids);
      const requested = new Set(options.Ids.split(','));
      const items = [
        {
          Id: 'episode-inherited',
          Type: 'Episode',
          SeriesId: 'series-1',
          SeriesPrimaryImageTag: 'series-tag',
          ParentPrimaryImageItemId: 'season-1',
          ParentPrimaryImageTag: 'season-tag'
        },
        {
          Id: 'episode-parent-fetch',
          Type: 'Episode',
          SeasonId: 'season-2',
          SeriesId: 'series-without-poster'
        },
        {
          Id: 'episode-type-hint',
          Type: 'Video',
          SeriesId: 'series-3',
          ImageTags: { Primary: 'generated-frame-tag' },
          PrimaryImageAspectRatio: 1.78
        },
        {
          Id: 'movie',
          Type: 'Movie',
          ImageTags: { Primary: 'movie-tag' },
          PrimaryImageAspectRatio: 0.67
        },
        {
          Id: 'movie-landscape-primary',
          Type: 'Movie',
          ImageTags: { Primary: 'movie-frame-tag' },
          PrimaryImageAspectRatio: 1.78
        },
        {
          Id: 'episode-no-poster',
          Type: 'Episode'
        },
        {
          Id: 'series-2',
          Type: 'Series',
          ImageTags: { Primary: 'series-2-tag' }
        },
        {
          Id: 'season-2',
          Type: 'Season',
          ImageTags: { Primary: 'season-2-tag' }
        },
        {
          Id: 'series-3',
          Type: 'Series',
          ImageTags: { Primary: 'series-3-tag' },
          PrimaryImageAspectRatio: 0.67
        }
      ];
      return { Items: items.filter((item) => requested.has(item.Id)) };
    }
  };

  const context = {
    console,
    Image: class {
      set src(url) {
        imageRequests.push(url);
        const isLoadable = /\/Items\/(series-1|season-2|series-3|movie)\/Images\/Primary/.test(url);
        setTimeout(() => (isLoadable ? this.onload?.() : this.onerror?.()), 0);
      }
    },
    document: {
      readyState: 'complete',
      documentElement: {
        setAttribute(name, value) {
          if (name === 'data-witzi-posters') helperStatus = value;
        }
      },
      querySelectorAll: () => cards,
      addEventListener() {}
    },
    MutationObserver: class {
      constructor(callback) { observerCallback = callback; }
      observe() {}
    },
    setTimeout,
    clearTimeout
  };
  context.window = {
    ApiClient: api,
    addEventListener() {},
    clearTimeout,
    requestAnimationFrame: (callback) => setTimeout(callback, 0),
    setTimeout: unrefTimeout
  };

  const source = await readFile(new URL('../src/witzi-posters.js', import.meta.url), 'utf8');
  vm.runInNewContext(source, context);
  await new Promise((resolve) => setTimeout(resolve, 30));

  assert.equal(helperStatus, 'active');
  assert.equal(calls.length, 1);
  assert.match(calls[0], /episode-inherited/);
  assert.equal(imageRequests.some((url) => url.includes('/Items/series-without-poster/Images/Primary')), true);
  assert.equal(imageRequests.some((url) => url.includes('/Items/season-2/Images/Primary')), true);

  for (const card of cards.slice(0, 4)) {
    assert.equal(card.dataset.witziArtwork, 'poster');
    assert.equal(card.classList.contains('witzi-poster-card'), true);
    assert.match(card.image.getAttribute('data-src'), /\/Images\/Primary/);
  }

  assert.match(cards[0].image.getAttribute('data-src'), /\/Items\/series-1\/Images\/Primary/);
  assert.match(cards[1].image.getAttribute('data-src'), /\/Items\/season-2\/Images\/Primary/);
  assert.match(cards[2].image.getAttribute('data-src'), /\/Items\/series-3\/Images\/Primary/);
  assert.match(cards[3].image.getAttribute('data-src'), /\/Items\/movie\/Images\/Primary/);

  for (const fallback of cards.slice(4)) {
    assert.equal(fallback.dataset.witziArtwork, 'fallback');
    assert.equal(fallback.classList.contains('witzi-poster-card'), false);
    assert.equal(fallback.classList.contains('witzi-native-fallback'), true);
    assert.match(fallback.image.getAttribute('data-src'), /\/Images\/Backdrop/);
    assert.match(fallback.image.style.backgroundImage, /\/Images\/Backdrop/);
  }

  cards[0].image.setAttribute('data-src', 'https://jellyfin.test/Items/episode-inherited/Images/Backdrop');
  cards[0].image.style.backgroundImage = 'url("https://jellyfin.test/Items/episode-inherited/Images/Backdrop")';
  observerCallback();
  await new Promise((resolve) => setTimeout(resolve, 10));

  assert.match(cards[0].image.getAttribute('data-src'), /\/Items\/series-1\/Images\/Primary/);
  assert.equal(calls.length, 1);
});

test('keeps portrait rows, joins the right toolbar, and reveals backdrops', async () => {
  const css = await readFile(new URL('../src/witzi-base.css', import.meta.url), 'utf8');

  assert.match(
    css,
    /\.backgroundContainer\.withBackdrop\s*\{[^}]*background:\s*transparent\s*!important;/s
  );
  assert.doesNotMatch(
    css,
    /\.backgroundContainer\.withBackdrop\s*\{[^}]*linear-gradient/s
  );
  assert.match(
    css,
    /html:has\(\.backgroundContainer\.withBackdrop\)[\s\S]*#reactRoot:has\(\.backgroundContainer\.withBackdrop\)[\s\S]*background-color:\s*transparent\s*!important;/
  );
  assert.match(
    css,
    /\.backdropImage\s*\{[^}]*filter:\s*blur\(2\.5px\) saturate\(0\.9\);[^}]*transform:\s*scale\(1\.025\);/s
  );
  assert.match(css, /@keyframes witzi-backdrop-fadein/);
  assert.match(
    css,
    /> \.backdropCard\s*\{\s*width:\s*33\.3333333333%\s*!important;/
  );
  assert.match(
    css,
    /\.cardPadder-backdrop,[\s\S]*padding-bottom:\s*150%\s*!important;/
  );
  assert.doesNotMatch(css, /\.backdropCard\.witzi-poster-card/);
  assert.doesNotMatch(css, /\.witzi-no-poster-card \.cardImageContainer/);
  assert.match(css, /\[data-monitor\*="videoplayback"\]\[data-monitor\*="markplayed"\]/);
  assert.match(css, /\.MuiBox-root:has\(\+ \.MuiBox-root\):not\(:empty\)/);
  assert.match(css, /\.MuiBox-root:has\(\+ \.MuiBox-root\):not\(:empty\)::before/);
  assert.doesNotMatch(css, /\.MuiBox-root:first-of-type/);
});

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

function createCard(id) {
  const attributes = new Map();
  const image = {
    style: {},
    setAttribute(name, value) { attributes.set(name, value); },
    getAttribute(name) { return attributes.get(name); },
    querySelector() { return null; }
  };

  return {
    dataset: { id },
    classList: new ClassList(),
    image,
    querySelector(selector) {
      return selector === '.cardImageContainer' ? image : null;
    }
  };
}

test('uses posters when available and retains backdrop fallbacks', async () => {
  const cards = ['episode-inherited', 'episode-parent-fetch', 'movie', 'episode-no-poster']
    .map(createCard);
  const calls = [];

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
          ParentPrimaryImageItemId: 'season-1',
          ParentPrimaryImageTag: 'season-tag'
        },
        {
          Id: 'episode-parent-fetch',
          Type: 'Episode',
          SeasonId: 'season-2',
          SeriesId: 'series-2'
        },
        {
          Id: 'movie',
          Type: 'Movie',
          ImageTags: { Primary: 'movie-tag' }
        },
        {
          Id: 'episode-no-poster',
          Type: 'Episode'
        },
        {
          Id: 'season-2',
          Type: 'Season',
          ImageTags: { Primary: 'season-2-tag' }
        }
      ];
      return { Items: items.filter((item) => requested.has(item.Id)) };
    }
  };

  const context = {
    console,
    document: {
      readyState: 'complete',
      documentElement: {},
      querySelectorAll: () => cards,
      addEventListener() {}
    },
    MutationObserver: class {
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
    setTimeout
  };

  const source = await readFile(new URL('../src/witzi-posters.js', import.meta.url), 'utf8');
  vm.runInNewContext(source, context);
  await new Promise((resolve) => setTimeout(resolve, 30));

  assert.equal(calls.length, 2);
  assert.match(calls[0], /episode-inherited/);
  assert.equal(calls[1], 'season-2,series-2');

  for (const card of cards.slice(0, 3)) {
    assert.equal(card.dataset.witziArtwork, 'poster');
    assert.equal(card.classList.contains('witzi-poster-card'), true);
    assert.match(card.image.getAttribute('data-src'), /\/Images\/Primary/);
  }

  const fallback = cards[3];
  assert.equal(fallback.dataset.witziArtwork, 'backdrop');
  assert.equal(fallback.classList.contains('witzi-poster-card'), false);
  assert.equal(fallback.image.getAttribute('data-src'), undefined);
});

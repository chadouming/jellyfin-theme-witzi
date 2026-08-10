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

async function waitFor(predicate, timeoutMs = 500) {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started >= timeoutMs) throw new Error('Timed out waiting for test state');
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
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

function createElement() {
  const attributes = new Map();
  const element = {
    children: [],
    classList: new ClassList(),
    nextSibling: null,
    parentNode: null,
    style: {},
    appendChild(child) {
      child.parentNode = this;
      this.children.push(child);
      return child;
    },
    getAttribute(name) { return attributes.get(name) || null; },
    removeAttribute(name) { attributes.delete(name); },
    setAttribute(name, value) { attributes.set(name, value); },
    querySelectorAll(selector) {
      return selector === '.backdropImage'
        ? this.children.filter((child) => child.classList.contains('backdropImage'))
        : [];
    }
  };
  return element;
}

function createBackdropImage(url) {
  const image = createElement();
  image.classList.add('backdropImage');
  image.setAttribute('data-url', url);
  image.style.backgroundImage = `url("${url}")`;
  return image;
}

test('uses loadable posters and retains native artwork when candidates fail', async () => {
  const cards = [
    createCard('episode-inherited', 'Episode'),
    createCard('episode-own-portrait', 'Episode'),
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
          Id: 'episode-own-portrait',
          Type: 'Episode',
          SeriesId: 'series-own',
          ImageTags: { Primary: 'witzi-generated-tag' },
          PrimaryImageAspectRatio: 0.6667,
          SeriesPrimaryImageTag: 'series-own-tag'
        },
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
        const isLoadable = /\/Items\/(series-1|episode-own-portrait|season-2|series-3|movie)\/Images\/Primary/.test(url);
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
  await new Promise((resolve) => setTimeout(resolve, 100));

  assert.equal(helperStatus, 'active');
  assert.equal(calls.length, 1);
  assert.match(calls[0], /episode-inherited/);
  assert.equal(imageRequests.some((url) => url.includes('/Items/series-without-poster/Images/Primary')), true);
  assert.equal(imageRequests.some((url) => url.includes('/Items/season-2/Images/Primary')), true);

  for (const card of cards.slice(0, 5)) {
    assert.equal(card.dataset.witziArtwork, 'poster');
    assert.equal(card.classList.contains('witzi-poster-card'), true);
    assert.match(card.image.getAttribute('data-src'), /\/Images\/Primary/);
  }

  assert.match(cards[0].image.getAttribute('data-src'), /\/Items\/series-1\/Images\/Primary/);
  assert.match(cards[1].image.getAttribute('data-src'), /\/Items\/episode-own-portrait\/Images\/Primary/);
  assert.match(cards[2].image.getAttribute('data-src'), /\/Items\/season-2\/Images\/Primary/);
  assert.match(cards[3].image.getAttribute('data-src'), /\/Items\/series-3\/Images\/Primary/);
  assert.match(cards[4].image.getAttribute('data-src'), /\/Items\/movie\/Images\/Primary/);

  for (const fallback of cards.slice(5)) {
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

test('keeps the current backdrop visible until a newer backdrop is ready', async () => {
  const urls = {
    first: 'https://jellyfin.test/Items/first/Images/Backdrop',
    slow: 'https://jellyfin.test/Items/slow/Images/Backdrop',
    newest: 'https://jellyfin.test/Items/newest/Images/Backdrop'
  };
  const container = createElement();
  container.classList.add('backdropContainer');
  container.appendChild(createBackdropImage(urls.first));

  const root = createElement();
  root.appendChild(container);
  root.insertBefore = function insertBefore(child, reference) {
    const oldIndex = this.children.indexOf(child);
    if (oldIndex >= 0) this.children.splice(oldIndex, 1);
    const index = this.children.indexOf(reference);
    this.children.splice(index < 0 ? this.children.length : index, 0, child);
    child.parentNode = this;
    this.children.forEach((entry, entryIndex) => {
      entry.nextSibling = this.children[entryIndex + 1] || null;
    });
    return child;
  };

  let observerCallback;
  let videoPlayer = null;
  const slowImages = [];
  const documentAttributes = new Map();
  const context = {
    console,
    Image: class {
      set src(url) {
        if (url === urls.slow) {
          slowImages.push(this);
        } else {
          setTimeout(() => this.onload?.(), 0);
        }
      }
    },
    document: {
      readyState: 'complete',
      documentElement: {
        removeAttribute(name) { documentAttributes.delete(name); },
        setAttribute(name, value) { documentAttributes.set(name, value); }
      },
      createElement,
      querySelector(selector) {
        if (selector === '.backdropContainer') return container;
        if (selector === '.videoPlayerContainer') return videoPlayer;
        return null;
      },
      querySelectorAll: () => [],
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
    addEventListener() {},
    clearTimeout,
    requestAnimationFrame: (callback) => setTimeout(callback, 0),
    setTimeout: unrefTimeout
  };

  const source = await readFile(new URL('../src/witzi-posters.js', import.meta.url), 'utf8');
  vm.runInNewContext(source, context);
  await waitFor(() => root.children[0]?.classList.contains('witzi-backdrop-cache'));

  const cache = root.children[0];
  const activeLayer = () => cache.children.find((layer) => (
    layer.classList.contains('witzi-backdrop-cache-active')
  ));
  await waitFor(() => Boolean(activeLayer()));

  assert.equal(cache.classList.contains('witzi-backdrop-cache'), true);
  assert.equal(cache.nextSibling, container);
  assert.equal(activeLayer().getAttribute('data-url'), urls.first);
  assert.equal(container.classList.contains('witzi-backdrop-cache-ready'), true);
  assert.equal(documentAttributes.get('data-witzi-backdrop-cache'), 'active');

  container.children = [];
  observerCallback();
  await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(activeLayer().getAttribute('data-url'), urls.first);

  container.appendChild(createBackdropImage(urls.slow));
  observerCallback();
  await waitFor(() => slowImages.length === 1);
  assert.equal(activeLayer().getAttribute('data-url'), urls.first);

  container.appendChild(createBackdropImage(urls.newest));
  observerCallback();
  await waitFor(() => activeLayer()?.getAttribute('data-url') === urls.newest);
  assert.equal(activeLayer().getAttribute('data-url'), urls.newest);

  slowImages[0].onload?.();
  await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(activeLayer().getAttribute('data-url'), urls.newest);

  videoPlayer = createElement();
  observerCallback();
  await waitFor(() => documentAttributes.get('data-witzi-video-active') === 'true');
  assert.equal(documentAttributes.get('data-witzi-video-active'), 'true');

  videoPlayer = null;
  observerCallback();
  await waitFor(() => !documentAttributes.has('data-witzi-video-active'));
  assert.equal(documentAttributes.has('data-witzi-video-active'), false);
});

test('moves live overview controls and metadata into the detail ribbon', async () => {
  const originalParent = {};
  const overview = { name: 'overview' };
  const overviewControls = { name: 'controls' };
  const sectionContent = {
    name: 'section-content',
    children: [overview, overviewControls],
    parentNode: originalParent
  };
  overview.parentNode = sectionContent;
  overviewControls.parentNode = sectionContent;
  const group = { name: 'metadata', parentNode: originalParent };
  let sectionContents = [sectionContent];
  let groups = [group];
  const buttons = {};
  let contentHost = null;
  let observerCallback = null;
  const ribbon = {
    insertBefore(host, reference) {
      assert.equal(reference, buttons);
      host.parentNode = this;
      contentHost = host;
    },
    querySelector(selector) {
      if (selector === '.witzi-ribbon-content') return contentHost;
      if (selector === '.mainDetailButtons') return buttons;
      return null;
    }
  };
  const pageAttributes = new Map();
  const page = {
    querySelector(selector) {
      if (selector === '.detailRibbon') return ribbon;
      if (selector === '.detailSectionContent') return sectionContents[0] || null;
      if (selector === '.itemDetailsGroup') return groups[0] || null;
      return null;
    },
    querySelectorAll(selector) {
      if (selector === '.detailSectionContent') return sectionContents;
      if (selector === '.itemDetailsGroup') return groups;
      return [];
    },
    removeAttribute(name) { pageAttributes.delete(name); },
    setAttribute(name, value) { pageAttributes.set(name, value); }
  };
  const context = {
    console,
    document: {
      readyState: 'complete',
      documentElement: { setAttribute() {}, removeAttribute() {} },
      createElement() {
        return {
          children: [],
          classList: new ClassList(),
          contains(child) {
            return this.children.includes(child);
          },
          replaceChildren(...children) {
            this.children = children;
            children.forEach((child) => { child.parentNode = this; });
          }
        };
      },
      querySelector(selector) {
        return selector === '#itemDetailPage' ? page : null;
      },
      querySelectorAll() { return []; },
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
    addEventListener() {},
    clearTimeout,
    requestAnimationFrame: (callback) => setTimeout(callback, 0),
    setTimeout: unrefTimeout
  };

  const source = await readFile(new URL('../src/witzi-posters.js', import.meta.url), 'utf8');
  vm.runInNewContext(source, context);
  await waitFor(() => contentHost?.children.length === 2);

  assert.equal(contentHost.classList.contains('witzi-ribbon-content'), true);
  assert.equal(contentHost.parentNode, ribbon);
  assert.equal(contentHost.children[0], sectionContent);
  assert.equal(contentHost.children[1], group);
  assert.equal(sectionContent.parentNode, contentHost);
  assert.equal(group.parentNode, contentHost);
  assert.equal(overview.parentNode, sectionContent);
  assert.equal(overviewControls.parentNode, sectionContent);
  assert.equal(pageAttributes.get('data-witzi-detail-content'), 'active');

  const replacementOverview = { name: 'replacement-overview' };
  const replacementSection = {
    name: 'replacement-section',
    children: [replacementOverview],
    parentNode: originalParent
  };
  replacementOverview.parentNode = replacementSection;
  const replacementGroup = { name: 'replacement-metadata', parentNode: originalParent };
  sectionContents = [sectionContent, replacementSection];
  groups = [group, replacementGroup];
  observerCallback();
  await waitFor(() => contentHost?.children[0] === replacementSection);

  assert.deepEqual(contentHost.children, [replacementSection, replacementGroup]);
  assert.equal(replacementSection.parentNode, contentHost);
  assert.equal(replacementGroup.parentNode, contentHost);
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
  assert.match(
    css,
    /\.witzi-backdrop-cache-layer\s*\{[^}]*opacity:\s*0;[^}]*transition:\s*opacity 800ms ease;/s
  );
  assert.match(css, /\.witzi-backdrop-cache-layer\.witzi-backdrop-cache-active\s*\{[^}]*opacity:\s*0\.66;/s);
  assert.match(css, /\.backdropContainer\.witzi-backdrop-cache-ready \.backdropImage/);
  assert.match(css, /html\[data-witzi-video-active="true"\] \.backgroundContainer/);
  assert.match(css, /html:has\(\.videoPlayerContainer\) \.witzi-backdrop-cache/);
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
  assert.match(css, /\.layout-mobile \.cardOverlayButton-br\[data-action="play"\]\s*\{[^}]*display:\s*none\s*!important;/s);
});

test('anchors series artwork and Next Up beside ribbon-first scrolling content', async () => {
  const css = await readFile(new URL('../src/witzi-base.css', import.meta.url), 'utf8');
  const helper = await readFile(new URL('../src/witzi-posters.js', import.meta.url), 'utf8');

  assert.match(
    css,
    /#itemDetailPage \.detailVerticalSection \.itemsContainer\.vertical-list > \.listItem\[data-type="Episode"\]/
  );
  assert.match(
    css,
    /\.listItem\[data-type="Episode"\][\s\S]*\.listItemImage-large\s*\{[^}]*aspect-ratio:\s*16 \/ 9;[^}]*flex:\s*0 0 var\(--witzi-detail-rail-width, clamp\(10rem, 17vw, 14rem\)\);/s
  );
  assert.match(css, /\.listItem\[data-type="Episode"\]:focus-within/);
  assert.match(
    css,
    /\.detailRibbon\s*\{[^}]*background-color:\s*var\(--witzi-surface\)\s*!important;[^}]*background-image:\s*none\s*!important;[^}]*border-radius:\s*1\.05rem;[^}]*backdrop-filter:\s*none;/s
  );
  assert.match(
    css,
    /\.layout-desktop \.detailRibbon\s*\{[^}]*display:\s*grid\s*!important;[^}]*"content content";[^}]*height:\s*auto\s*!important;[^}]*margin-top:\s*calc\(var\(--witzi-detail-poster-top\) - var\(--witzi-detail-backdrop-height\)\)\s*!important;[^}]*min-height:\s*clamp\(7\.6rem, 15vh, 9rem\);[^}]*overflow:\s*hidden;[^}]*padding-block:\s*0\.65rem 0;/s
  );
  assert.match(
    css,
    /margin-left:\s*var\(--witzi-detail-content-start\);/
  );
  assert.match(css, /padding-left:\s*var\(--witzi-detail-ribbon-inner-padding\);/);
  assert.match(
    css,
    /#itemDetailPage \.detailImageContainer \.card:hover \.cardBox\s*\{[^}]*filter:\s*none;[^}]*transform:\s*none;/s
  );
  assert.match(
    css,
    /--witzi-header-height:\s*2\.9rem;[\s\S]*--witzi-header-control:\s*2\.5rem;[\s\S]*--witzi-header-radius:\s*0\.72rem;/
  );
  assert.match(
    css,
    /\.MuiAppBar-root > \.MuiToolbar-root:first-child > \.MuiBox-root:has\(\+ \.MuiBox-root\):not\(:empty\)::before\s*\{[^}]*border-radius:\s*var\(--witzi-header-radius\);/s
  );
  assert.match(
    css,
    /\.layout-desktop #itemDetailPage \.itemBackdrop\s*\{[^}]*height:\s*var\(--witzi-detail-backdrop-height\);/s
  );
  assert.match(
    css,
    /\.layout-desktop #itemDetailPage\s*\{[^}]*--witzi-detail-rail-width:\s*clamp\(12rem, min\(25\.5vw, 36vh\), 21rem\);[^}]*--witzi-detail-poster-height:\s*calc\(var\(--witzi-detail-rail-width\) \* 1\.5\);[^}]*--witzi-detail-rail-top:\s*calc\(var\(--witzi-header-height\) \+ clamp\(0\.2rem, 0\.45vh, 0\.35rem\)\);[^}]*--witzi-detail-poster-top:\s*var\(--witzi-detail-rail-top\);[^}]*--witzi-detail-next-up-top:/s
  );
  assert.doesNotMatch(
    css,
    /margin-(?:left|right):\s*calc\(var\(--witzi-detail-content-start\) - var\(--witzi-detail-ribbon-inner-padding\)\);/
  );
  assert.match(
    css,
    /#itemDetailPage \.detailLogo\s*\{[^}]*display:\s*none\s*!important;/s
  );
  assert.doesNotMatch(css, /#itemDetailPage \.detailLogo\s*\{[^}]*position:\s*fixed;/s);
  assert.match(
    css,
    /\.layout-desktop #itemDetailPage \.detailImageContainer\.hide-mobile \.card\s*\{[^}]*position:\s*fixed;[^}]*top:\s*var\(--witzi-detail-poster-top\)\s*!important;[^}]*width:\s*var\(--witzi-detail-rail-width\)\s*!important;/s
  );
  assert.match(
    css,
    /\.layout-desktop #itemDetailPage \.nextUpSection:not\(\.hide\)\s*\{[^}]*bottom:\s*0\.75rem;[^}]*max-height:\s*none;[^}]*position:\s*fixed;[^}]*top:\s*var\(--witzi-detail-next-up-top\);[^}]*width:\s*var\(--witzi-detail-rail-width\);/s
  );
  assert.match(
    css,
    /#itemDetailPage:has\(\.nextUpSection:not\(\.hide\)\)\s*\{[^}]*--witzi-detail-rail-width:\s*clamp\(12rem, min\(28vw, calc\(48\.5vh - 4rem\)\), 48rem\);/s
  );
  assert.match(
    css,
    /\.nextUpSection:not\(\.hide\) \.nextUpItems > \.card:not\(:first-child\)\s*\{[^}]*display:\s*none\s*!important;/s
  );
  assert.match(
    css,
    /#itemDetailPage:not\(:has\(\.nextUpSection:not\(\.hide\)\)\) \.trackSelections:not\(\.hide\)\s*\{[^}]*display:\s*grid;[^}]*bottom:\s*0\.75rem;[^}]*max-height:\s*none;[^}]*position:\s*fixed;[^}]*top:\s*var\(--witzi-detail-next-up-top\);[^}]*width:\s*var\(--witzi-detail-rail-width\);/s
  );
  assert.match(
    css,
    /#itemDetailPage:not\(:has\(\.nextUpSection:not\(\.hide\)\)\):has\(\.trackSelections:not\(\.hide\)\)\s*\{[^}]*--witzi-detail-rail-width:\s*clamp\(14rem, min\(28vw, 38vh\), 30rem\);/s
  );
  assert.match(
    css,
    /#itemDetailPage:not\(:has\(\.nextUpSection:not\(\.hide\)\)\) \.trackSelections:not\(\.hide\) \.trackSelectionFieldContainer:not\(\.hide\)\s*\{[^}]*display:\s*grid\s*!important;[^}]*grid-template-columns:\s*minmax\(0, 1fr\)\s*!important;[^}]*width:\s*100%;/s
  );
  assert.match(
    css,
    /\.trackSelections:not\(\.hide\) \.detailTrackSelect\s*\{[^}]*max-width:\s*none\s*!important;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /#itemDetailPage \.detailPagePrimaryContent,[\s\S]*#itemDetailPage \.detailPageSecondaryContainer\s*\{[^}]*padding-left:\s*var\(--witzi-detail-content-start\);/s
  );
  assert.match(
    css,
    /\.layout-desktop #itemDetailPage:has\(#listChildrenCollapsible:not\(\.hide\) \.listItem\[data-type="Episode"\]\)\s*\{[^}]*--witzi-detail-backdrop-height:\s*clamp\(15rem, 30vh, 20rem\);/s
  );
  assert.doesNotMatch(css, /max-height:\s*64rem/);
  assert.match(
    css,
    /#itemDetailPage:has\(#listChildrenCollapsible:not\(\.hide\) \.listItem\[data-type="Episode"\]\) \.detailSectionContent \.overview\s*\{[^}]*-webkit-line-clamp:\s*3;/s
  );
  assert.match(
    css,
    /#itemDetailPage:has\(#listChildrenCollapsible:not\(\.hide\) \.listItem\[data-type="Episode"\]\) #listChildrenCollapsible\s*\{[^}]*margin-top:\s*0;/s
  );
  assert.doesNotMatch(
    css,
    /#itemDetailPage:has\(#listChildrenCollapsible:not\(\.hide\) \.listItem\[data-type="Episode"\]\) \.itemDetailsGroup\s*\{[^}]*margin-top:/s
  );
  assert.match(
    css,
    /#itemDetailPage \.itemTags,[\s\S]*#itemDetailPage \.itemExternalLinks,[\s\S]*#itemDetailPage \.itemGenres\s*\{[^}]*display:\s*none\s*!important;/s
  );
  assert.match(
    css,
    /\.mediaInfoText\.mediaInfoOfficialRating\s*\{[^}]*color:\s*inherit\s*!important;/s
  );
  assert.match(
    css,
    /#itemDetailPage:not\(\[data-witzi-detail-content="active"\]\) \.detailRibbon\s*\{[^}]*border-bottom:\s*0;[^}]*border-bottom-left-radius:\s*0;/s
  );
  assert.match(
    css,
    /#itemDetailPage:not\(\[data-witzi-detail-content="active"\]\) \.detailSectionContent,[\s\S]*#itemDetailPage:not\(\[data-witzi-detail-content="active"\]\) \.itemDetailsGroup\s*\{[^}]*background-color:\s*var\(--witzi-surface\)\s*!important;[^}]*background-image:\s*none\s*!important;[^}]*border-left:[^}]*margin:\s*0\s*!important;[^}]*max-width:\s*100%\s*!important;[^}]*overflow:\s*hidden;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /#itemDetailPage:not\(\[data-witzi-detail-content="active"\]\) \.itemDetailsGroup\s*\{[^}]*align-items:\s*stretch;[^}]*border-bottom:[^}]*display:\s*flex;[^}]*flex-direction:\s*column;[^}]*flex-wrap:\s*nowrap;/s
  );
  assert.match(css, /\.witzi-ribbon-content \.overview\s*\{[^}]*line-height:\s*1\.42;[^}]*margin:\s*0;/s);
  assert.match(
    css,
    /\.witzi-ribbon-content \.detailSectionContent\s*\{[^}]*background-color:\s*transparent\s*!important;[^}]*border:\s*0\s*!important;[^}]*box-sizing:\s*border-box;[^}]*display:\s*grid;[^}]*margin:\s*0\s*!important;[^}]*max-width:\s*100%\s*!important;[^}]*overflow:\s*hidden;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /\.witzi-ribbon-content \.itemDetailsGroup\s*\{[^}]*align-items:\s*stretch;[^}]*border:\s*0\s*!important;[^}]*display:\s*flex;[^}]*flex-direction:\s*column;[^}]*flex-wrap:\s*nowrap;[^}]*margin:\s*0\s*!important;[^}]*max-width:\s*100%\s*!important;[^}]*overflow:\s*hidden;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /\.witzi-ribbon-content\s*\{[^}]*gap:\s*0\.12rem;[^}]*max-width:\s*100%;[^}]*overflow:\s*hidden;[^}]*width:\s*100%;/s
  );
  assert.match(
    css,
    /\.itemDetailsGroup > \.MuiBox-root\.css-0,[\s\S]*\.itemDetailsGroup \.detailsGroupItem\.MuiBox-root\.css-0\s*\{[^}]*width:\s*100%;/s
  );
  assert.match(
    css,
    /\.layout-desktop \.detailRibbon\s*\{[^}]*align-content:\s*start;[^}]*row-gap:\s*0\.15rem;/s
  );
  assert.match(
    css,
    /\.detailRibbon \.infoWrapper \.itemMiscInfo\s*\{[^}]*margin-bottom:\s*0\.15rem\s*!important;/s
  );
  assert.match(
    css,
    /#itemDetailPage\[data-witzi-detail-content="active"\]:has\(\.nextUpSection:not\(\.hide\)\) #listChildrenCollapsible:not\(\.hide\),[\s\S]*order:\s*-20;/s
  );
  assert.match(helper, /function detailContentCandidate\(page, host, selector\)/);
  assert.match(helper, /candidates\.find\(\(element\) => !host\.contains\?\.\(element\)\)/);
  assert.match(helper, /host\.replaceChildren\(\.\.\.content\)/);
  assert.match(helper, /function syncDetailRibbonContent\(\)/);
  assert.match(helper, /const content = \[sectionContent, group\]\.filter\(Boolean\);/);
  assert.match(helper, /pages\.forEach\(syncDetailRibbonPage\)/);
  assert.match(helper, /setAttribute\('data-witzi-detail-content', 'active'\)/);
});

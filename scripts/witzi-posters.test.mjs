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

test('waits for Jellyfin to load Custom CSS before starting the helper', async () => {
  let helperStatus;
  let observerCount = 0;
  let themeActive = false;
  const context = {
    console,
    document: {
      readyState: 'complete',
      documentElement: {
        removeAttribute() {},
        setAttribute(name, value) {
          if (name === 'data-witzi-posters') helperStatus = value;
        }
      },
      querySelector() { return null; },
      querySelectorAll() { return []; },
      addEventListener() {}
    },
    MutationObserver: class {
      observe() { observerCount += 1; }
    },
    setTimeout,
    clearTimeout
  };
  context.window = {
    addEventListener() {},
    clearTimeout,
    getComputedStyle() {
      return {
        getPropertyValue(name) {
          return name === '--witzi-theme-active' && themeActive ? '1' : '';
        }
      };
    },
    requestAnimationFrame: (callback) => setTimeout(callback, 0),
    setTimeout: unrefTimeout
  };

  const source = await readFile(new URL('../src/witzi-posters.js', import.meta.url), 'utf8');
  vm.runInNewContext(source, context);
  vm.runInNewContext(source, context);

  assert.equal(context.window.__witziPosterHelperLoaded, true);
  assert.equal(helperStatus, 'waiting');
  assert.equal(observerCount, 0);

  themeActive = true;
  await waitFor(() => helperStatus === 'active', 1000);

  assert.equal(observerCount, 1);
});

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

test('moves all live detail content into one ribbon panel', async () => {
  const originalParent = {};
  const info = { name: 'info', parentNode: null };
  const buttons = { name: 'buttons', parentNode: null };
  const overview = {
    name: 'overview',
    closest(selector) { return selector === '.detailSectionContent' ? sectionContent : null; }
  };
  const overviewControls = { name: 'controls' };
  const sectionContent = {
    name: 'section-content',
    children: [overview, overviewControls],
    parentNode: originalParent
  };
  overview.parentNode = sectionContent;
  overviewControls.parentNode = sectionContent;
  const group = { name: 'metadata', parentNode: originalParent };
  let infos = [info];
  let buttonGroups = [buttons];
  let sectionContents = [sectionContent];
  let groups = [group];
  let observerCallback = null;
  let detailQueryCount = 0;
  const ribbon = {
    children: [info, buttons],
    contains(child) {
      let current = child;
      while (current) {
        if (current === this) return true;
        current = current.parentNode;
      }
      return false;
    },
    insertBefore(child, reference) {
      const currentIndex = this.children.indexOf(child);
      if (currentIndex >= 0) this.children.splice(currentIndex, 1);
      const referenceIndex = reference ? this.children.indexOf(reference) : -1;
      const index = referenceIndex >= 0 ? referenceIndex : this.children.length;
      this.children.splice(index, 0, child);
      child.parentNode = this;
    },
    removeChild(child) {
      const index = this.children.indexOf(child);
      if (index >= 0) this.children.splice(index, 1);
      child.parentNode = null;
    },
    querySelector(selector) {
      if (selector === '.witzi-ribbon-content') return null;
      if (selector === '.mainDetailButtons') return buttons;
      return null;
    }
  };
  info.parentNode = ribbon;
  buttons.parentNode = ribbon;
  const pageAttributes = new Map();
  const page = {
    querySelector(selector) {
      if (selector === '.detailRibbon') return ribbon;
      if (selector === '.infoWrapper') return infos[0] || null;
      if (selector === '.mainDetailButtons') return buttonGroups[0] || null;
      if (selector === '.detailSectionContent') return sectionContents[0] || null;
      if (selector === '.itemDetailsGroup') return groups[0] || null;
      return null;
    },
    querySelectorAll(selector) {
      detailQueryCount += 1;
      if (selector === '.infoWrapper') return infos;
      if (selector === '.mainDetailButtons') return buttonGroups;
      if (selector === '.overview') return [overview];
      if (selector === '.overview.detail-clamp-text') return [overview];
      if (selector === '.detailPagePrimaryContent .overview.detail-clamp-text') return [overview];
      if (selector === '.detailSectionContent') return sectionContents;
      if (selector === '.detailPagePrimaryContent .detailSectionContent') return sectionContents;
      if (selector === '.itemDetailsGroup') return groups;
      if (selector === '.detailPagePrimaryContent .itemDetailsGroup') return groups;
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
          insertBefore(child, reference) {
            const currentIndex = this.children.indexOf(child);
            if (currentIndex >= 0) this.children.splice(currentIndex, 1);
            const referenceIndex = reference ? this.children.indexOf(reference) : -1;
            const index = referenceIndex >= 0 ? referenceIndex : this.children.length;
            this.children.splice(index, 0, child);
            child.parentNode = this;
          },
          removeChild(child) {
            const index = this.children.indexOf(child);
            if (index >= 0) this.children.splice(index, 1);
            child.parentNode = null;
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
  await waitFor(() => ribbon.children.length === 4);

  assert.equal(ribbon.children[0], info);
  assert.equal(ribbon.children[1], buttons);
  assert.equal(ribbon.children[2], sectionContent);
  assert.equal(ribbon.children[3], group);
  assert.equal(info.parentNode, ribbon);
  assert.equal(buttons.parentNode, ribbon);
  assert.equal(sectionContent.parentNode, ribbon);
  assert.equal(group.parentNode, ribbon);
  assert.equal(overview.parentNode, sectionContent);
  assert.equal(overviewControls.parentNode, sectionContent);
  assert.equal(pageAttributes.get('data-witzi-detail-content'), 'active');

  await new Promise((resolve) => setTimeout(resolve, 70));
  const queriesBeforeStyleMutation = detailQueryCount;
  observerCallback([{ type: 'attributes', attributeName: 'style' }]);
  await new Promise((resolve) => setTimeout(resolve, 10));
  assert.equal(detailQueryCount, queriesBeforeStyleMutation);

  const replacementOverview = {
    name: 'replacement-overview',
    closest(selector) { return selector === '.detailSectionContent' ? replacementSection : null; }
  };
  const replacementSection = {
    name: 'replacement-section',
    children: [replacementOverview],
    parentNode: originalParent
  };
  replacementOverview.parentNode = replacementSection;
  const replacementGroup = { name: 'replacement-metadata', parentNode: originalParent };
  const replacementInfo = { name: 'replacement-info', parentNode: originalParent };
  const replacementButtons = { name: 'replacement-buttons', parentNode: originalParent };
  infos = [info, replacementInfo];
  buttonGroups = [buttons, replacementButtons];
  sectionContents = [sectionContent, replacementSection];
  groups = [group, replacementGroup];
  page.querySelectorAll = function querySelectorAll(selector) {
    if (selector === '.infoWrapper') return infos;
    if (selector === '.mainDetailButtons') return buttonGroups;
    if (selector === '.overview') return [overview, replacementOverview];
    if (selector === '.overview.detail-clamp-text') return [overview, replacementOverview];
    if (selector === '.detailPagePrimaryContent .overview.detail-clamp-text') return [overview, replacementOverview];
    if (selector === '.detailSectionContent') return sectionContents;
    if (selector === '.detailPagePrimaryContent .detailSectionContent') return sectionContents;
    if (selector === '.itemDetailsGroup') return groups;
    if (selector === '.detailPagePrimaryContent .itemDetailsGroup') return groups;
    return [];
  };
  observerCallback();
  await waitFor(() => ribbon.children[0] === replacementInfo);

  assert.deepEqual(ribbon.children, [replacementInfo, replacementButtons, replacementSection, replacementGroup]);
  assert.equal(replacementInfo.parentNode, ribbon);
  assert.equal(replacementButtons.parentNode, ribbon);
  assert.equal(replacementSection.parentNode, ribbon);
  assert.equal(replacementGroup.parentNode, ribbon);
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
  assert.match(
    css,
    /\.emby-scroller\s*\{[^}]*-ms-overflow-style:\s*none;[^}]*scrollbar-width:\s*none;/s
  );
  assert.match(
    css,
    /\.emby-scroller::\-webkit-scrollbar\s*\{[^}]*display:\s*none;[^}]*height:\s*0;[^}]*width:\s*0;/s
  );
  assert.match(
    css,
    /\.emby-scrollbuttons\s*\{[^}]*background-color:\s*var\(--witzi-surface\)\s*!important;[^}]*border:\s*1px solid[^;]+;[^}]*color:\s*var\(--witzi-text\)\s*!important;/s
  );
  assert.match(
    css,
    /\.emby-scrollbuttons \.emby-scrollbuttons-button:not\(:disabled\):is\(:hover, :focus-visible, :active\)\s*\{[^}]*background-color:\s*var\(--witzi-accent\)\s*!important;[^}]*color:\s*var\(--witzi-on-accent\)\s*!important;/s
  );
});

test('anchors series artwork and Next Up beside ribbon-first scrolling content', async () => {
  const css = await readFile(new URL('../src/witzi-base.css', import.meta.url), 'utf8');
  const helper = await readFile(new URL('../src/witzi-posters.js', import.meta.url), 'utf8');

  assert.match(css, /--witzi-theme-active:\s*1;/);
  assert.match(helper, /function startWhenThemeIsReady\(\)/);
  assert.match(helper, /data-witzi-posters', 'waiting'/);

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
    /\.layout-desktop \.detailRibbon\s*\{[^}]*display:\s*flex\s*!important;[^}]*flex-direction:\s*column;[^}]*gap:\s*0\.12rem;[^}]*height:\s*auto\s*!important;[^}]*margin-top:\s*calc\(\s*var\(--witzi-detail-top-padding\)\s*-\s*var\(--witzi-detail-backdrop-height\)\s*-\s*var\(--witzi-header-height\)\s*\)\s*!important;[^}]*min-height:\s*clamp\(7\.6rem, 15vh, 9rem\);[^}]*overflow:\s*hidden;[^}]*padding-block:\s*0\.65rem 0;/s
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
    /\.layout-desktop #itemDetailPage\s*\{[^}]*--witzi-detail-rail-width:\s*clamp\(12rem, min\(25\.5vw, 36vh\), 21rem\);[^}]*--witzi-detail-poster-height:\s*calc\(var\(--witzi-detail-rail-width\) \* 1\.5\);[^}]*--witzi-detail-top-padding:\s*clamp\(0\.65rem, 1vh, 0\.9rem\);[^}]*--witzi-detail-rail-top:\s*calc\(var\(--witzi-header-height\) \+ var\(--witzi-detail-top-padding\)\);[^}]*--witzi-detail-poster-top:\s*var\(--witzi-detail-rail-top\);[^}]*--witzi-detail-next-up-top:/s
  );
  assert.match(
    css,
    /\.layout-desktop \.detailPagePrimaryContainer\s*\{[^}]*display:\s*flow-root\s*!important;[^}]*padding-top:\s*0\s*!important;/s
  );
  assert.match(
    css,
    /\.layout-desktop #itemDetailPage \.detailPageWrapperContainer\s*\{[^}]*margin-top:\s*0\s*!important;/s
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
    /\.detailImageContainer\.hide-mobile \.cardBox,[\s\S]*\.detailImageContainer\.hide-mobile \.cardScalable\s*\{[^}]*margin-top:\s*0\s*!important;[^}]*padding-top:\s*0\s*!important;/s
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
    /#itemDetailPage :is\(\s*\.detailPagePrimaryContainer,[\s\S]*#listChildrenCollapsible,[\s\S]*#childrenContent\s*\)\s*\{[^}]*align-self:\s*stretch;[^}]*max-width:\s*none\s*!important;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /:is\(\s*\.detailPagePrimaryContent,[\s\S]*\.detailPageSecondaryContainer\s*\) > :is\(\.detailSection, \.detailVerticalSection\)\s*\{[^}]*max-width:\s*none\s*!important;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /:is\(\.detailPagePrimaryContent, \.detailPageSecondaryContainer\) \.horizontalSection\s*\{[^}]*margin-inline:\s*0\s*!important;[^}]*overflow-x:\s*clip;[^}]*width:\s*100%;/s
  );
  assert.match(
    css,
    /:is\(\.detailPagePrimaryContent, \.detailPageSecondaryContainer\) \.emby-scroller\s*\{[^}]*margin-inline:\s*0\s*!important;[^}]*overflow-x:\s*auto;[^}]*overscroll-behavior-inline:\s*contain;[^}]*width:\s*100%\s*!important;/s
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
  assert.doesNotMatch(css, /#itemDetailPage:not\(\[data-witzi-detail-content="active"\]\) \.detailRibbon/);
  assert.match(css, /\.detailRibbon \.overview\s*\{[^}]*line-height:\s*1\.42;[^}]*margin:\s*0;/s);
  assert.match(
    css,
    /\.detailRibbon > \.detailSectionContent\s*\{[^}]*background-color:\s*transparent\s*!important;[^}]*border:\s*0\s*!important;[^}]*box-sizing:\s*border-box;[^}]*display:\s*grid;[^}]*margin:\s*0\s*!important;[^}]*max-width:\s*100%\s*!important;[^}]*overflow:\s*hidden;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /\.detailRibbon > \.itemDetailsGroup\s*\{[^}]*align-items:\s*stretch;[^}]*border:\s*0\s*!important;[^}]*display:\s*flex;[^}]*flex-direction:\s*column;[^}]*flex-wrap:\s*nowrap;[^}]*margin:\s*0\s*!important;[^}]*max-width:\s*100%\s*!important;[^}]*overflow:\s*hidden;[^}]*padding:\s*0 0 0\.65rem\s*!important;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /\.detailRibbon > :is\(\.infoWrapper, \.mainDetailButtons, \.detailSectionContent, \.itemDetailsGroup\)\s*\{[^}]*background-color:\s*transparent\s*!important;[^}]*background-image:\s*none\s*!important;[^}]*flex:\s*0 0 auto;[^}]*width:\s*100%\s*!important;/s
  );
  assert.match(
    css,
    /\.itemDetailsGroup > \.MuiBox-root\.css-0,[\s\S]*\.itemDetailsGroup \.detailsGroupItem\.MuiBox-root\.css-0\s*\{[^}]*width:\s*100%;/s
  );
  assert.doesNotMatch(css, /\.layout-desktop \.detailRibbon\s*\{[^}]*grid-template-areas:/s);
  assert.match(
    css,
    /\.detailRibbon \.infoWrapper \.itemMiscInfo\s*\{[^}]*margin-bottom:\s*0\.15rem\s*!important;/s
  );
  assert.match(
    css,
    /#itemDetailPage\[data-witzi-detail-content="active"\]:has\(\.nextUpSection:not\(\.hide\)\) #listChildrenCollapsible:not\(\.hide\),[\s\S]*order:\s*-20;/s
  );
  assert.match(helper, /function detailContentCandidate\(page, host, selector, sourceSelector = selector\)/);
  assert.doesNotMatch(helper, /DETAIL_RIBBON_CORRECTION|getBoundingClientRect|style\.setProperty/);
  assert.match(helper, /function scheduleDetail\(\)/);
  assert.match(helper, /function scheduleMedia\(\)/);
  assert.match(helper, /function mutationChangesDetailLayout\(mutation\)/);
  assert.match(helper, /\['class', 'data-id'\]\.includes\(mutation\.attributeName\)/);
  assert.match(helper, /candidates\.find\(\(element\) => !host\.contains\?\.\(element\)\)/);
  assert.match(helper, /function syncDetailRibbonChildren\(ribbon, content, managedContent\)/);
  assert.match(helper, /content\.forEach\(\(element\) => ribbon\.insertBefore\(element, null\)\)/);
  assert.match(helper, /function syncDetailRibbonContent\(\)/);
  assert.match(helper, /const info = detailContentCandidate\(page, ribbon, '\.infoWrapper'\);/);
  assert.match(helper, /const buttons = detailContentCandidate\(page, ribbon, '\.mainDetailButtons'\);/);
  assert.match(helper, /'\.overview\.detail-clamp-text'/);
  assert.match(helper, /const content = \[info, buttons, sectionContent, group\]\.filter\(Boolean\);/);
  assert.match(helper, /sectionContent\.parentNode === ribbon/);
  assert.match(helper, /group\.parentNode === ribbon/);
  assert.match(helper, /pages\.forEach\(syncDetailRibbonPage\)/);
  assert.match(helper, /setAttribute\('data-witzi-detail-content', 'active'\)/);
  assert.match(helper, /window\.addEventListener\('resize', schedule\)/);
});

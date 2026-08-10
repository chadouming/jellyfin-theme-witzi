/**
 * Witzi poster helper for Jellyfin Web.
 *
 * Jellyfin builds Continue Watching and Next Up with landscape artwork. CSS
 * cannot change the API-selected image URL, so this optional helper asks the
 * current Jellyfin ApiClient for each card's metadata and swaps in a real
 * poster. Episodes prefer the series' main Primary poster, then a season/parent
 * poster; their own widescreen Primary capture is never used. Movies use their
 * own portrait Primary poster. A candidate must load successfully before it
 * replaces Jellyfin's native artwork, which remains visible as the last resort.
 */
(function witziPosterHelper() {
  'use strict';

  if (window.__witziPosterHelperLoaded) return;
  window.__witziPosterHelperLoaded = true;
  document.documentElement?.setAttribute?.('data-witzi-posters', 'active');

  const CARD_SELECTOR = '.itemsContainer[data-monitor*="videoplayback"][data-monitor*="markplayed"] .card[data-id]';
  const itemCache = new Map();
  const retryAfter = new Map();
  const pendingCards = new WeakSet();
  const MISSING_RETRY_MS = 30000;
  const POSTER_LOAD_TIMEOUT_MS = 8000;
  let retryTimer;
  let scheduled = false;

  function getApiClient() {
    return window.ApiClient || null;
  }

  function ownPoster(item) {
    const tag = item?.ImageTags?.Primary;
    const aspect = Number(item?.PrimaryImageAspectRatio);
    const isLandscapeFrame = Number.isFinite(aspect) && aspect > 1;
    return tag && item.Id && !isLandscapeFrame ? { id: item.Id, tag } : null;
  }

  function inheritedPosters(item) {
    const candidates = [
      item?.SeriesId && {
        id: item.SeriesId,
        tag: item.SeriesPrimaryImageTag || null
      },
      item?.ParentPrimaryImageItemId && {
        id: item.ParentPrimaryImageItemId,
        tag: item.ParentPrimaryImageTag || null
      },
      item?.SeasonId && { id: item.SeasonId, tag: null }
    ].filter(Boolean);

    return candidates.filter((poster, index) => (
      candidates.findIndex((candidate) => candidate.id === poster.id) === index
    ));
  }

  function posterUrl(api, poster) {
    const options = {
      type: 'Primary',
      maxWidth: 600,
      quality: 90
    };

    if (poster.tag) options.tag = poster.tag;

    return typeof api.getScaledImageUrl === 'function'
      ? api.getScaledImageUrl(poster.id, options)
      : api.getImageUrl(poster.id, options);
  }

  async function fetchItems(api, ids) {
    const userId = api.getCurrentUserId?.();
    if (!userId || typeof api.getItems !== 'function') {
      throw new Error('Jellyfin ApiClient is not ready');
    }

    const response = await api.getItems(userId, {
      Ids: ids.join(','),
      Fields: 'PrimaryImageAspectRatio,ParentId',
      EnableImages: true,
      EnableImageTypes: 'Primary',
      ImageTypeLimit: 1
    });

    return response?.Items || [];
  }

  function isEpisode(item, typeHint) {
    return typeHint?.toLowerCase() === 'episode'
      || item?.Type?.toLowerCase() === 'episode'
      || Boolean(item?.SeriesId);
  }

  function canLoad(url) {
    return new Promise((resolve) => {
      const image = new Image();
      let settled = false;

      const finish = (loaded) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timeout);
        image.onload = null;
        image.onerror = null;
        resolve(loaded);
      };

      const timeout = window.setTimeout(() => finish(false), POSTER_LOAD_TIMEOUT_MS);
      image.onload = () => finish(true);
      image.onerror = () => finish(false);
      image.src = url;
    });
  }

  async function firstLoadablePoster(api, candidates) {
    for (const poster of candidates) {
      const url = posterUrl(api, poster);
      if (await canLoad(url)) return url;
    }

    return null;
  }

  async function resolvePosters(api, typeHints) {
    const ids = [...typeHints.keys()];
    const now = Date.now();
    const missingIds = ids.filter((id) => (
      !itemCache.has(id) && now >= (retryAfter.get(id) || 0)
    ));

    if (missingIds.length) {
      const items = await fetchItems(api, missingIds);
      const itemsById = new Map(items.map((item) => [item.Id, item]));

      await Promise.all(missingIds.map(async (id) => {
        const item = itemsById.get(id);
        const own = item && ownPoster(item);
        const candidates = isEpisode(item, typeHints.get(id))
          ? inheritedPosters(item)
          : own ? [own] : [];
        const url = await firstLoadablePoster(api, candidates);

        if (url) {
          itemCache.set(id, url);
          retryAfter.delete(id);
        } else {
          retryAfter.set(id, Date.now() + MISSING_RETRY_MS);
        }
      }));
    }

    return new Map(ids.map((id) => [id, itemCache.get(id) || null]));
  }

  function applyPoster(card, url) {
    const image = card.querySelector('.cardImageContainer');
    if (!image) return false;

    image.setAttribute('data-src', url);
    image.removeAttribute?.('data-blurhash');
    image.classList?.remove('lazy');
    image.style.backgroundImage = `url("${url.replace(/["\\]/g, '\\$&')}")`;
    image.style.backgroundPosition = 'center';
    image.style.backgroundRepeat = 'no-repeat';
    image.style.backgroundSize = 'cover';

    const nestedImage = image.querySelector('img');
    if (nestedImage) {
      nestedImage.src = url;
      nestedImage.removeAttribute?.('srcset');
      nestedImage.removeAttribute?.('data-src');
      nestedImage.removeAttribute?.('data-srcset');
    }

    card.dataset.witziArtwork = 'poster';
    card.dataset.witziPosterId = card.dataset.id;
    card.classList.add('witzi-poster-card');
    card.classList.remove('witzi-poster-pending');
    card.classList.remove('witzi-no-poster-card');
    card.classList.remove('witzi-native-fallback');
    return true;
  }

  function markPending(card) {
    card.classList.add('witzi-poster-pending');
  }

  function markMissing(card) {
    card.dataset.witziArtwork = 'fallback';
    card.dataset.witziPosterId = card.dataset.id;
    card.classList.remove('witzi-poster-card');
    card.classList.remove('witzi-poster-pending');
    card.classList.add('witzi-native-fallback');
  }

  function retryLater(delay = 1500) {
    window.clearTimeout(retryTimer);
    retryTimer = window.setTimeout(schedule, delay);
  }

  function hasAppliedPoster(card, url) {
    const image = card.querySelector('.cardImageContainer');
    return card.dataset.witziArtwork === 'poster'
      && card.dataset.witziPosterId === card.dataset.id
      && image?.getAttribute('data-src') === url
      && image.style.backgroundImage.includes(url);
  }

  function needsProcessing(card) {
    const id = card.dataset.id;
    if (!id || pendingCards.has(card)) return false;

    const url = itemCache.get(id);
    if (url) return !hasAppliedPoster(card, url);

    return card.dataset.witziPosterId !== id
      || card.dataset.witziArtwork !== 'fallback'
      || Date.now() >= (retryAfter.get(id) || 0);
  }

  async function processCards() {
    const cards = [...document.querySelectorAll(CARD_SELECTOR)]
      .filter(needsProcessing);

    if (!cards.length) return;

    cards.forEach(markPending);

    const api = getApiClient();
    if (!api) {
      retryLater();
      return;
    }

    cards.forEach((card) => pendingCards.add(card));
    const typeHints = new Map(cards.map((card) => [card.dataset.id, card.dataset.type]));

    try {
      const posters = await resolvePosters(api, typeHints);

      for (const card of cards) {
        const url = posters.get(card.dataset.id);
        if (!url || !applyPoster(card, url)) markMissing(card);
      }

      if (cards.some((card) => card.dataset.witziArtwork === 'fallback')) {
        retryLater(MISSING_RETRY_MS);
      }
    } catch (error) {
      cards.forEach(markMissing);
      console.warn('[witzi-posters] Poster lookup failed; retaining native card artwork.', error);
      retryLater();
    } finally {
      cards.forEach((card) => pendingCards.delete(card));
    }
  }

  function schedule() {
    if (scheduled) return;
    scheduled = true;
    window.requestAnimationFrame(() => {
      scheduled = false;
      void processCards();
    });
  }

  function start() {
    const observer = new MutationObserver(schedule);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-id', 'data-src', 'style'],
      childList: true,
      subtree: true
    });
    window.addEventListener('viewshow', schedule);
    window.addEventListener('pageshow', schedule);
    schedule();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }
}());

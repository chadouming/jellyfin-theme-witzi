/**
 * Witzi poster helper for Jellyfin Web.
 *
 * Jellyfin builds Continue Watching and Next Up with landscape artwork. CSS
 * cannot change the API-selected image URL, so this optional helper asks the
 * current Jellyfin ApiClient for each card's metadata and swaps in a real
 * poster. Episodes prefer the series' main Primary poster, then a season/parent
 * poster; their own widescreen Primary capture is never used. Movies use their
 * own portrait Primary poster. Generated landscape frames are hidden when no
 * real poster can be found.
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

  function inheritedPoster(item) {
    if (item?.SeriesId) {
      return {
        id: item.SeriesId,
        tag: item.SeriesPrimaryImageTag || null
      };
    }

    if (item?.ParentPrimaryImageItemId) {
      return {
        id: item.ParentPrimaryImageItemId,
        tag: item.ParentPrimaryImageTag || null
      };
    }

    if (item?.SeasonId) return { id: item.SeasonId, tag: null };

    return null;
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

  async function resolvePosters(api, typeHints) {
    const ids = [...typeHints.keys()];
    const now = Date.now();
    const missingIds = ids.filter((id) => (
      !itemCache.has(id) && now >= (retryAfter.get(id) || 0)
    ));

    if (missingIds.length) {
      const items = await fetchItems(api, missingIds);
      const itemsById = new Map(items.map((item) => [item.Id, item]));
      const unresolvedEpisodes = [];

      for (const id of missingIds) {
        const item = itemsById.get(id);
        let poster = null;

        if (isEpisode(item, typeHints.get(id))) {
          poster = inheritedPoster(item);
          if (!poster) {
            unresolvedEpisodes.push({
              id,
              parentIds: [...new Set([
                item?.SeriesId,
                item?.ParentPrimaryImageItemId,
                item?.SeasonId
              ].filter(Boolean))]
            });
            continue;
          }
        } else if (item) {
          poster = ownPoster(item);
        }

        if (poster) {
          itemCache.set(id, posterUrl(api, poster));
          retryAfter.delete(id);
        } else {
          retryAfter.set(id, now + MISSING_RETRY_MS);
        }
      }

      if (unresolvedEpisodes.length) {
        const parentIds = [...new Set(unresolvedEpisodes.flatMap((episode) => episode.parentIds))];
        const parents = parentIds.length ? await fetchItems(api, parentIds) : [];
        const parentsById = new Map(parents.map((item) => [item.Id, item]));

        for (const episode of unresolvedEpisodes) {
          const poster = episode.parentIds
            .map((id) => ownPoster(parentsById.get(id)))
            .find(Boolean);
          if (poster) {
            itemCache.set(episode.id, posterUrl(api, poster));
            retryAfter.delete(episode.id);
          } else {
            retryAfter.set(episode.id, now + MISSING_RETRY_MS);
          }
        }
      }
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
    return true;
  }

  function markPending(card) {
    card.classList.add('witzi-poster-pending');
  }

  function markMissing(card) {
    const image = card.querySelector('.cardImageContainer');
    if (image) {
      image.removeAttribute?.('data-src');
      image.style.backgroundImage = 'none';
    }

    card.dataset.witziArtwork = 'missing';
    card.dataset.witziPosterId = card.dataset.id;
    card.classList.remove('witzi-poster-card');
    card.classList.remove('witzi-poster-pending');
    card.classList.add('witzi-no-poster-card');
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
      || card.dataset.witziArtwork !== 'missing'
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

      if (cards.some((card) => card.dataset.witziArtwork === 'missing')) {
        retryLater(MISSING_RETRY_MS);
      }
    } catch (error) {
      cards.forEach(markMissing);
      console.warn('[witzi-posters] Poster lookup failed; hiding generated video frames.', error);
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

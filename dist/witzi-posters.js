/**
 * Witzi poster helper for Jellyfin Web.
 *
 * Jellyfin builds Continue Watching and Next Up with landscape artwork. CSS
 * cannot change the API-selected image URL, so this optional helper asks the
 * current Jellyfin ApiClient for each card's metadata and swaps in a real
 * poster. Episodes prefer the series' main Primary poster, then a season/parent
 * poster; their own widescreen Primary capture is never used. Movies use their
 * own Primary poster. When no inherited poster is available, the native image
 * remains as a contained fallback.
 */
(function witziPosterHelper() {
  'use strict';

  if (window.__witziPosterHelperLoaded) return;
  window.__witziPosterHelperLoaded = true;

  const CARD_SELECTOR = '.homeSectionsContainer .itemsContainer[data-monitor="videoplayback,markplayed"] > .card[data-id]';
  const itemCache = new Map();
  const pendingCards = new WeakSet();
  let retryTimer;
  let scheduled = false;

  function getApiClient() {
    return window.ApiClient || null;
  }

  function ownPoster(item) {
    const tag = item?.ImageTags?.Primary;
    return tag && item.Id ? { id: item.Id, tag } : null;
  }

  function inheritedPoster(item) {
    if (item?.SeriesId && item.SeriesPrimaryImageTag) {
      return {
        id: item.SeriesId,
        tag: item.SeriesPrimaryImageTag
      };
    }

    if (item?.ParentPrimaryImageItemId && item.ParentPrimaryImageTag) {
      return {
        id: item.ParentPrimaryImageItemId,
        tag: item.ParentPrimaryImageTag
      };
    }

    return null;
  }

  function posterUrl(api, poster) {
    const options = {
      type: 'Primary',
      tag: poster.tag,
      maxWidth: 600,
      quality: 90
    };

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
      Fields: 'PrimaryImageAspectRatio',
      EnableImages: true
    });

    return response?.Items || [];
  }

  async function resolvePosters(api, ids) {
    const missingIds = ids.filter((id) => !itemCache.has(id));

    if (missingIds.length) {
      const items = await fetchItems(api, missingIds);
      const itemsById = new Map(items.map((item) => [item.Id, item]));
      const unresolvedEpisodes = [];

      for (const id of missingIds) {
        const item = itemsById.get(id);
        let poster = null;

        if (item?.Type === 'Episode') {
          poster = inheritedPoster(item);
          if (!poster) {
            unresolvedEpisodes.push({
              id,
              parentIds: [...new Set([item.SeriesId, item.SeasonId].filter(Boolean))]
            });
            continue;
          }
        } else if (item) {
          poster = ownPoster(item);
        }

        itemCache.set(id, poster ? posterUrl(api, poster) : null);
      }

      if (unresolvedEpisodes.length) {
        const parentIds = [...new Set(unresolvedEpisodes.flatMap((episode) => episode.parentIds))];
        const parents = parentIds.length ? await fetchItems(api, parentIds) : [];
        const parentsById = new Map(parents.map((item) => [item.Id, item]));

        for (const episode of unresolvedEpisodes) {
          const poster = episode.parentIds
            .map((id) => ownPoster(parentsById.get(id)))
            .find(Boolean);
          itemCache.set(episode.id, poster ? posterUrl(api, poster) : null);
        }
      }
    }

    return new Map(ids.map((id) => [id, itemCache.get(id) || null]));
  }

  function applyPoster(card, url) {
    const image = card.querySelector('.cardImageContainer');
    if (!image) return false;

    image.setAttribute('data-src', url);
    image.style.backgroundImage = `url("${url.replace(/["\\]/g, '\\$&')}")`;
    image.style.backgroundPosition = 'center';
    image.style.backgroundRepeat = 'no-repeat';
    image.style.backgroundSize = 'cover';

    const nestedImage = image.querySelector('img');
    if (nestedImage) nestedImage.src = url;

    card.dataset.witziArtwork = 'poster';
    card.classList.add('witzi-poster-card');
    return true;
  }

  function markBackdrop(card) {
    card.dataset.witziArtwork = 'backdrop';
    card.classList.remove('witzi-poster-card');
  }

  function retryLater() {
    window.clearTimeout(retryTimer);
    retryTimer = window.setTimeout(schedule, 1500);
  }

  async function processCards() {
    const cards = [...document.querySelectorAll(CARD_SELECTOR)]
      .filter((card) => !card.dataset.witziArtwork && !pendingCards.has(card));

    if (!cards.length) return;

    const api = getApiClient();
    if (!api) {
      retryLater();
      return;
    }

    cards.forEach((card) => pendingCards.add(card));
    const ids = [...new Set(cards.map((card) => card.dataset.id).filter(Boolean))];

    try {
      const posters = await resolvePosters(api, ids);

      for (const card of cards) {
        const url = posters.get(card.dataset.id);
        if (!url || !applyPoster(card, url)) markBackdrop(card);
      }
    } catch (error) {
      console.warn('[witzi-posters] Poster lookup failed; retaining backdrop cards.', error);
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
    observer.observe(document.documentElement, { childList: true, subtree: true });
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

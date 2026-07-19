// Minimal service worker for VLMS: installability ("Add to Home Screen") plus
// app-shell asset caching for faster repeat loads.
//
// Scope, deliberately: Vlms.Web is a Blazor Web App using **Server** interactivity
// (see openwiki/web.md/architecture.md) — the UI is rendered server-side and kept
// live over a SignalR circuit, so there is no meaningful "offline app" to build here
// the way there is for a Blazor WebAssembly PWA (whose template generates a
// service-worker-assets.js build manifest and can run the whole app disconnected —
// see Microsoft Learn's "ASP.NET Core Blazor Progressive Web Application (PWA)").
// None of that offline-execution machinery applies to a Server-interactive app: without
// a live connection to the server there is no app to run. This service worker is
// therefore scoped to exactly two things:
//   1. Making the app installable (a registered service worker is one of the standard
//      browser installability signals, alongside the manifest below).
//   2. Cache-first, populate-as-you-go caching of same-origin static assets (CSS, JS,
//      images, fonts, the manifest itself) so a repeat visit fetches them from the
//      cache instead of the network.
// It never intercepts navigation requests (the server-rendered HTML document itself,
// which is dynamic and per-request — auth state, antiforgery tokens, etc. — and must
// never be served stale from a cache) and it never touches the SignalR circuit
// (WebSocket upgrades aren't dispatched through the Fetch API's 'fetch' event at all,
// so there is nothing to explicitly exclude for that, but the destination allow-list
// below means non-asset requests are left alone regardless).

const CACHE_NAME = "vlms-static-v1";
const CACHEABLE_DESTINATIONS = ["style", "script", "image", "font", "manifest"];

self.addEventListener("install", (event) => {
  // Take over from any previous version as soon as it's installed; there is no
  // offline app state to preserve consistently across versions here (see above),
  // so the usual "wait until all tabs close" caution for offline-first PWAs doesn't
  // apply in the same way.
  self.skipWaiting();
  event.waitUntil(caches.open(CACHE_NAME));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))
        )
      )
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;

  if (request.method !== "GET") {
    return;
  }

  // Never intercept navigation (the server-rendered document) or anything
  // cross-origin — only same-origin static assets are candidates for caching.
  if (request.mode === "navigate") {
    return;
  }

  let url;
  try {
    url = new URL(request.url);
  } catch {
    return;
  }

  if (url.origin !== self.location.origin) {
    return;
  }

  if (!CACHEABLE_DESTINATIONS.includes(request.destination)) {
    return;
  }

  event.respondWith(
    caches.open(CACHE_NAME).then(async (cache) => {
      const cached = await cache.match(request);
      if (cached) {
        return cached;
      }

      const response = await fetch(request);
      if (response.ok) {
        cache.put(request, response.clone());
      }
      return response;
    })
  );
});

// Service worker for VLMS: installability ("Add to Home Screen"), app-shell asset caching and
// lessons saved for offline use (functional.md FR-023, adr/0011).
//
// Scope, deliberately: Vlms.Web is a Blazor Web App using **Server** interactivity
// (see openwiki/web.md/architecture.md) — the UI is rendered server-side and kept
// live over a SignalR circuit, so there is no "offline app" to build the way there is for a
// Blazor WebAssembly PWA. Without a connection the only thing that works is the lesson content a
// person chose to save, and that is all this worker does beyond installability and asset caching:
//   1. Making the app installable (a registered service worker is one of the standard
//      browser installability signals, alongside the manifest).
//   2. Cache-first, populate-as-you-go caching of same-origin static assets (CSS, JS,
//      images, fonts, the manifest itself) so a repeat visit fetches them from the
//      cache instead of the network. The cache name carries the build id (the `v` in this script's
//      URL, set by App.razor), so every build starts with an empty cache and the old one is deleted
//      on activate: an unversioned asset can never be served stale for ever.
//   3. Lesson files (/lessons/files/{id}) and a converted lesson's pictures (/lessons/pictures/{id},
//      /lessons/teachers-information/pictures/{id}; adr/0012): network first, falling back to the copy
//      saved on the device in the "vlms-lessons" cache. The page script (offline-lessons.js) writes and removes
//      that cache; this worker only reads it and must never delete it.
//   4. Navigation: network first, so a page is never served stale while online; with no connection
//      the saved copy of that page if there is one (a converted lesson's page, adr/0012 decision 7),
//      otherwise the precached /offline.html, which lists the saved lessons. Static assets a saved
//      lesson page needs (its stylesheets and scripts) are also kept in "vlms-lessons", so they are
//      found there when the static cache no longer has them (a new build) and there is no connection.
// It never touches the SignalR circuit (WebSocket upgrades aren't dispatched through the Fetch
// API's 'fetch' event at all).

const BUILD = new URL(self.location.href).searchParams.get("v") || "dev";
const STATIC_PREFIX = "vlms-static-";
const CACHE_NAME = STATIC_PREFIX + BUILD;
const LESSONS_CACHE = "vlms-lessons";
const CACHEABLE_DESTINATIONS = ["style", "script", "image", "font", "manifest"];
const LESSON_FILE = /^\/lessons\/(files\/\d+|pictures\/\d+|teachers-information\/pictures\/\d+)$/;
const OFFLINE_PAGE = "/offline.html";
const PRECACHE = [OFFLINE_PAGE, "/offline.js", "/app.css"];

// The content type each kind of static request must come back with before it is cached. A server
// fault can answer 200 with an empty or wrong body (seen 25/10/2026, a wrong content root); caching
// that would keep the app broken in the browser after the server is fixed.
const EXPECTED_TYPES = {
  style: "text/css",
  script: "javascript",
  image: "image/",
  font: "font/",
  manifest: "json",
  document: "text/html",
};

function expectedTypeFor(path) {
  if (path.endsWith(".css")) return EXPECTED_TYPES.style;
  if (path.endsWith(".js")) return EXPECTED_TYPES.script;
  return EXPECTED_TYPES.document;
}

async function worthCaching(response, expectedType) {
  if (!response.ok || response.type !== "basic") {
    return false;
  }
  const contentType = response.headers.get("content-type") || "";
  if (!expectedType || !contentType.includes(expectedType)) {
    return false;
  }
  const body = await response.clone().arrayBuffer();
  return body.byteLength > 0;
}

self.addEventListener("install", (event) => {
  // Take over from any previous version as soon as it's installed; nothing in the static cache
  // needs to stay consistent across versions, and the saved lessons live in their own cache.
  // A precache file that comes back bad fails the install, so the previous worker stays in charge.
  self.skipWaiting();
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) =>
      Promise.all(
        PRECACHE.map(async (path) => {
          const response = await fetch(new Request(path, { cache: "reload" }));
          if (!(await worthCaching(response, expectedTypeFor(path)))) {
            throw new Error("Not caching a bad response for " + path);
          }
          await cache.put(path, response);
        })
      )
    )
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter((key) => key.startsWith(STATIC_PREFIX) && key !== CACHE_NAME)
            .map((key) => caches.delete(key))
        )
      )
      .then(() => self.clients.claim())
  );
});

async function lessonFile(request) {
  try {
    return await fetch(request);
  } catch (error) {
    const saved = await caches.open(LESSONS_CACHE).then((cache) => cache.match(request.url, { ignoreVary: true }));
    if (saved) {
      return saved;
    }
    if (request.mode === "navigate") {
      return offlinePage(error);
    }
    throw error;
  }
}

async function savedCopy(request) {
  return caches.open(LESSONS_CACHE).then((cache) => cache.match(request.url, { ignoreVary: true }));
}

async function savedPageOrOfflinePage(request, error) {
  const saved = await savedCopy(request);
  if (saved) {
    return saved;
  }
  return offlinePage(error);
}

async function offlinePage(error) {
  const page = await caches.open(CACHE_NAME).then((cache) => cache.match(OFFLINE_PAGE));
  if (page) {
    return page;
  }
  throw error;
}

self.addEventListener("fetch", (event) => {
  const request = event.request;

  if (request.method !== "GET") {
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

  if (LESSON_FILE.test(url.pathname)) {
    event.respondWith(lessonFile(request));
    return;
  }

  // The server-rendered document is dynamic per request (auth state, antiforgery tokens): always
  // the network, and the offline page only when there is no connection at all.
  if (request.mode === "navigate") {
    event.respondWith(fetch(request).catch((error) => savedPageOrOfflinePage(request, error)));
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

      let response;
      try {
        response = await fetch(request);
      } catch (error) {
        const saved = await savedCopy(request);
        if (saved) {
          return saved;
        }
        throw error;
      }
      if (await worthCaching(response, EXPECTED_TYPES[request.destination])) {
        cache.put(request, response.clone());
      }
      return response;
    })
  );
});

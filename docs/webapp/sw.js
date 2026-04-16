const CACHE_NAME = 'pdf-viewer-v3';
const PRECACHE_URLS = [
  './',
  './pdf-viewer.html',
  './comic-viewer.html',
  './manifest.webmanifest',
  './icons/icon-192.png',
  './icons/icon-512.png',
  './icons/icon-maskable-512.png',
  './vendor/pdfjs/pdf.min.mjs',
  './vendor/pdfjs/pdf.worker.min.mjs',
  './vendor/pica/pica.js',
  './vendor/libarchive/libarchive.js',
  './vendor/libarchive/worker-bundle.js',
  './vendor/libarchive/libarchive.wasm',
  './vendor/vips/vips-es6.js',
  './vendor/vips/vips.wasm',
];

self.addEventListener('install', (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE_NAME);
    await Promise.all(PRECACHE_URLS.map(async (url) => {
      try {
        const resp = await fetch(url, { cache: 'reload' });
        if (resp.ok) await cache.put(url, resp);
      } catch (e) {
        console.warn('[sw] precache failed:', url, e.message);
      }
    }));
    self.skipWaiting();
  })());
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k)));
    await self.clients.claim();
  })());
});

self.addEventListener('message', (ev) => {
  if (ev.data && ev.data.type === 'SKIP_WAITING') self.skipWaiting();
});

function withCoiHeaders(response) {
  if (!response || response.status === 0) return response;
  const headers = new Headers(response.headers);
  headers.set('Cross-Origin-Embedder-Policy', 'require-corp');
  headers.set('Cross-Origin-Opener-Policy', 'same-origin');
  headers.set('Cross-Origin-Resource-Policy', 'cross-origin');
  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;

  event.respondWith((async () => {
    const cache = await caches.open(CACHE_NAME);
    const cached = await cache.match(req, { ignoreSearch: true });
    if (cached) return withCoiHeaders(cached);
    try {
      const fresh = await fetch(req);
      if (fresh.ok && fresh.type === 'basic') {
        cache.put(req, fresh.clone()).catch(() => {});
      }
      return withCoiHeaders(fresh);
    } catch (e) {
      return new Response('Offline and not cached', { status: 503, statusText: 'Offline' });
    }
  })());
});

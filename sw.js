// Imajination Service Worker — v1.1
const CACHE_NAME = 'imajination-v1.5';
const STATIC_ASSETS = [
  '/',
  '/pages/home/LandingPage.html',
  '/pages/browse/Artists.html',
  '/pages/browse/Events.html',
  '/pages/browse/Sessionists.html',
  '/pages/auth/login.html',
  '/assets/css/style.css',
  '/assets/js/theme.js',
  '/assets/js/system-dialogs.js',
  '/assets/js/performance-helpers.js',
  '/assets/js/navbar-notifications.js',
  '/assets/js/navbar-profile-menu.js',
  '/manifest.json'
];

// Only cache same-origin http/https requests
function isCacheable(url) {
  try {
    const u = new URL(url);
    return (u.protocol === 'http:' || u.protocol === 'https:') &&
           !u.hostname.includes('chrome-extension') &&
           u.hostname !== 'fonts.googleapis.com' && // let fonts handle themselves
           u.hostname !== 'cdn.tailwindcss.com';    // never cache CDN JS bundles
  } catch {
    return false;
  }
}

// ── Install: cache static shell ──────────────────────────────────────────────
self.addEventListener('install', event => {
  self.skipWaiting(); // take over immediately on every update
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then(cache => {
        return Promise.allSettled(
          STATIC_ASSETS.map(url =>
            cache.add(new Request(url, { cache: 'reload' })).catch(() => {})
          )
        );
      })
  );
});

// ── Activate: clear old caches + claim all clients immediately ────────────────
self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(
        keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k))
      ))
      .then(() => self.clients.claim())
      .then(() => {
        // Tell all open tabs to hard-reload now that a new SW is active
        return self.clients.matchAll({ type: 'window', includeUncontrolled: true });
      })
      .then(clients => {
        clients.forEach(client => {
          client.postMessage({ type: 'SW_UPDATED' });
        });
      })
  );
});

// ── Fetch strategy ───────────────────────────────────────────────────────────
self.addEventListener('fetch', event => {
  const { request } = event;

  // Skip non-http/https (chrome-extension://, etc.)
  if (!isCacheable(request.url)) return;

  const url = new URL(request.url);

  if (url.pathname === '/pages/tools/dashboardscanner.html' || url.pathname === '/dashboardscanner.html') {
    event.respondWith(fetch(request));
    return;
  }

  // Always go network for API calls
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/')) {
    event.respondWith(
      fetch(request).catch(() =>
        new Response(JSON.stringify({ message: 'You are offline.' }), {
          status: 503,
          headers: { 'Content-Type': 'application/json' }
        })
      )
    );
    return;
  }

  // Never cache app HTML pages. We want nav updates to appear immediately.
  if (request.mode === 'navigate' || url.pathname.endsWith('.html')) {
    event.respondWith(
      fetch(request, { cache: 'no-store' })
        .catch(() =>
          caches.match(request)
            .then(cached => cached || caches.match('/pages/home/LandingPage.html'))
        )
    );
    return;
  }

  // Navigation — network first, fall back to cache
  if (request.mode === 'navigate') {
    event.respondWith(
      fetch(request)
        .then(response => {
          if (response.ok && isCacheable(request.url)) {
            const clone = response.clone();
            caches.open(CACHE_NAME).then(cache => cache.put(request, clone));
          }
          return response;
        })
        .catch(() =>
          caches.match(request)
            .then(cached => cached || caches.match('/pages/home/LandingPage.html'))
        )
    );
    return;
  }

  // Static assets — cache first, network fallback
  if (
    url.pathname.startsWith('/assets/') ||
    url.pathname === '/manifest.json' ||
    request.destination === 'image' ||
    request.destination === 'style' ||
    request.destination === 'script' ||
    request.destination === 'font'
  ) {
    event.respondWith(
      caches.match(request).then(cached => {
        if (cached) return cached;
        return fetch(request).then(response => {
          if (response.ok && isCacheable(request.url)) {
            const clone = response.clone();
            caches.open(CACHE_NAME).then(cache => cache.put(request, clone));
          }
          return response;
        });
      })
    );
    return;
  }

  // Everything else — network first
  event.respondWith(
    fetch(request).catch(() => caches.match(request))
  );
});

// ── Push notifications ───────────────────────────────────────────────────────
self.addEventListener('push', event => {
  if (!event.data) return;
  let payload;
  try { payload = event.data.json(); } catch { payload = { title: 'Imajination', body: event.data.text() }; }
  event.waitUntil(
    self.registration.showNotification(payload.title || 'Imajination', {
      body: payload.body || '',
      icon: '/assets/images/logo.png',
      badge: '/assets/images/logo.png',
      data: { url: payload.url || '/' },
      vibrate: [200, 100, 200]
    })
  );
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const url = event.notification.data?.url || '/';
  event.waitUntil(
    clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clientList => {
      const existing = clientList.find(c => c.url === url && 'focus' in c);
      if (existing) return existing.focus();
      return clients.openWindow(url);
    })
  );
});

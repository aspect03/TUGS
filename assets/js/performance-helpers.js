(function () {
  const inflightRequests = new Map();
  const fetchCachePrefix = 'imajinationFetchCache:';
  const defaultApiCacheTtlMs = 30 * 1000;
  let csrfToken = null;
  let csrfTokenPromise = null;
  let sessionValidationPromise = null;
  let sessionTimeoutHandle = null;
  let sessionMonitorHandle = null;
  const sessionIdleLimitMs = 30 * 60 * 1000;
  const sessionMonitorIntervalMs = 15 * 1000;
  const apiFallbackBases = resolveApiFallbackBases();
  const protectedPathPrefixes = [
    '/pages/dashboards/',
    '/pages/bookings/',
    '/pages/tools/dashboardscanner.html'
  ];

  function resolveApiFallbackBases() {
    // Never use production fallbacks when running locally — they cause CORS errors
    const isLocal = ['localhost', '127.0.0.1', '0.0.0.0'].includes(window.location.hostname);
    const configured = [
      window.__IMAJINATION_API_BASE__,
      localStorage.getItem('imajinationApiBase'),
      ...(isLocal ? [] : [
        'https://imajination.onrender.com',
        'https://imajination-api.onrender.com'
      ])
    ];

    const normalized = [];
    configured.forEach((value) => {
      if (typeof value !== 'string') return;
      const trimmed = value.trim().replace(/\/+$/, '');
      if (!trimmed || normalized.includes(trimmed)) return;
      normalized.push(trimmed);
    });

    return normalized;
  }

  function toApiFallbackUrl(url, baseUrl) {
    try {
      const parsed = new URL(url, window.location.origin);
      if (!parsed.pathname.startsWith('/api/')) return null;
      return `${baseUrl}${parsed.pathname}${parsed.search}`;
    } catch {
      return null;
    }
  }

  function shouldRetryWithFallback(response, options) {
    if (response?.status === 404 && options?.skipApiFallbackOn404) {
      return false;
    }

    if (!response) return true;
    return response.status === 404
      || response.status === 502
      || response.status === 503
      || response.status === 504;
  }

  async function fetchWithApiFallback(resource, options, primaryFetch) {
    const requestUrl = typeof resource === 'string' ? resource : resource?.url || '';
    if (!isSameOriginApiRequest(requestUrl)) {
      return primaryFetch(resource, options);
    }

    let primaryResponse = null;
    try {
      primaryResponse = await primaryFetch(resource, options);
      if (!shouldRetryWithFallback(primaryResponse, options)) {
        return primaryResponse;
      }
    } catch {
      primaryResponse = null;
    }

    for (const baseUrl of apiFallbackBases) {
      const fallbackUrl = toApiFallbackUrl(requestUrl, baseUrl);
      if (!fallbackUrl || fallbackUrl.startsWith(window.location.origin)) {
        continue;
      }

      try {
        const fallbackResource = resource instanceof Request
          ? new Request(fallbackUrl, resource)
          : fallbackUrl;
        const fallbackResponse = await primaryFetch(fallbackResource, options);
        if (!shouldRetryWithFallback(fallbackResponse, options)) {
          return fallbackResponse;
        }
        primaryResponse = fallbackResponse;
      } catch {
        // Try the next backend candidate.
      }
    }

    if (primaryResponse) {
      return primaryResponse;
    }

    throw new Error('Unable to reach the API backend.');
  }

  function isSameOriginApiRequest(url) {
    try {
      const parsed = new URL(url, window.location.origin);
      return parsed.origin === window.location.origin && parsed.pathname.startsWith('/api/');
    } catch {
      return false;
    }
  }

  function shouldCacheRequest(url, options) {
    const method = (options?.method || 'GET').toUpperCase();
    if (method !== 'GET') return false;

    try {
      const parsed = new URL(url, window.location.origin);
      if (parsed.origin !== window.location.origin) return false;
      if (!parsed.pathname.startsWith('/api/')) return false;
      // Never cache: auth, security, or real-time availability endpoints
      if (parsed.pathname === '/api/security/csrf-token') return false;
      if (parsed.pathname === '/api/auth/session-status') return false;
      if (parsed.pathname.endsWith('/tiers')) return false;
      // Never cache single-event detail (slot counts change as tickets are sold)
      if (/^\/api\/event\/[0-9a-f-]{36}$/.test(parsed.pathname)) return false;
      return true;
    } catch {
      return false;
    }
  }

  function buildCacheKey(url) {
    return `${fetchCachePrefix}${url}`;
  }

  function readCachedResponse(url) {
    try {
      const raw = sessionStorage.getItem(buildCacheKey(url));
      if (!raw) return null;

      const parsed = JSON.parse(raw);
      if (!parsed || typeof parsed.savedAt !== 'number' || typeof parsed.body !== 'string') return null;
      if (Date.now() - parsed.savedAt > defaultApiCacheTtlMs) return null;

      return parsed;
    } catch {
      return null;
    }
  }

  function writeCachedResponse(url, response, bodyText) {
    try {
      sessionStorage.setItem(buildCacheKey(url), JSON.stringify({
        savedAt: Date.now(),
        body: bodyText,
        status: response.status,
        statusText: response.statusText,
        headers: Array.from(response.headers.entries())
      }));
    } catch {
      // Ignore storage pressure and keep the app working.
    }
  }

  function createResponseFromCache(cached) {
    return new Response(cached.body, {
      status: cached.status || 200,
      statusText: cached.statusText || 'OK',
      headers: cached.headers || { 'Content-Type': 'application/json' }
    });
  }

  function shouldAttachSessionToken(url) {
    try {
      const parsed = new URL(url, window.location.origin);
      return parsed.origin === window.location.origin && parsed.pathname.startsWith('/api/');
    } catch {
      return false;
    }
  }

  function installSecurityFetch() {
    if (window.__imajinationSecurityFetchInstalled) return;
    window.__imajinationSecurityFetchInstalled = true;

    const nativeFetch = window.fetch.bind(window);
    window.__imajinationNativeFetch = nativeFetch;

    async function ensureCsrfToken() {
      if (csrfToken) return csrfToken;
      if (csrfTokenPromise) return csrfTokenPromise;

      csrfTokenPromise = fetchWithApiFallback('/api/security/csrf-token', {
        method: 'GET',
        credentials: 'same-origin',
        headers: {
          'X-Requested-With': 'fetch'
        }
      }, nativeFetch)
        .then(async (response) => {
          if (!response.ok) {
            throw new Error('Failed to initialize request security.');
          }

          const data = await response.json().catch(() => ({}));
          csrfToken = data.token || '';
          return csrfToken;
        })
        .finally(() => {
          csrfTokenPromise = null;
        });

      return csrfTokenPromise;
    }

    window.fetch = async function securedFetch(resource, options = {}) {
      const originalUrl = typeof resource === 'string' ? resource : resource?.url || '';
      const method = (options?.method || (resource instanceof Request ? resource.method : 'GET') || 'GET').toUpperCase();
      const isApiRequest = isSameOriginApiRequest(originalUrl);
      const requiresCsrf = !['GET', 'HEAD', 'OPTIONS', 'TRACE'].includes(method) && isApiRequest;
      const headers = new Headers(resource instanceof Request ? resource.headers : undefined);

      if (options.headers) {
        new Headers(options.headers).forEach((value, key) => headers.set(key, value));
      }

      if (isApiRequest) {
        if (!headers.has('X-Requested-With')) {
          headers.set('X-Requested-With', 'fetch');
        }

        if (requiresCsrf) {
          const token = await ensureCsrfToken();
          if (!headers.has('X-CSRF-TOKEN') && token) {
            headers.set('X-CSRF-TOKEN', token);
          }
        }

        const actorUserId = localStorage.getItem('userId');
        const actorRole = localStorage.getItem('userRole');
        if (actorUserId && !headers.has('X-Actor-UserId')) {
          headers.set('X-Actor-UserId', actorUserId);
        }
        if (actorRole && !headers.has('X-Actor-Role')) {
          headers.set('X-Actor-Role', actorRole);
        }
        const sessionToken = (localStorage.getItem('sessionToken') || '').trim();
        if (sessionToken && shouldAttachSessionToken(originalUrl) && !headers.has('X-Session-Token')) {
          headers.set('X-Session-Token', sessionToken);
        }
      }

      const finalOptions = {
        ...options,
        headers,
        credentials: options.credentials || 'same-origin'
      };

      const finalResource = resource instanceof Request
        ? new Request(resource, finalOptions)
        : resource;

      const response = await fetchWithApiFallback(finalResource, finalOptions, nativeFetch);
      return handleUnauthorizedApiResponse(response, originalUrl);
    };
  }

  function installFetchOptimizer() {
    if (window.__imajinationFetchOptimized) return;
    window.__imajinationFetchOptimized = true;

    const nativeFetch = window.fetch.bind(window);

    window.fetch = async function optimizedFetch(resource, options = {}) {
      const url = typeof resource === 'string' ? resource : resource?.url || '';
      const canCache = shouldCacheRequest(url, options) && options.cache !== 'no-store';

      if (!canCache) {
        return nativeFetch(resource, options);
      }

      const cacheKey = `${(options.method || 'GET').toUpperCase()}:${url}`;
      if (inflightRequests.has(cacheKey)) {
        return inflightRequests.get(cacheKey).then(response => response.clone());
      }

      const cached = readCachedResponse(url);
      if (cached) {
        const cachedResponse = createResponseFromCache(cached);
        const revalidatePromise = nativeFetch(resource, options)
          .then(async response => {
            if (response.ok) {
              const clone = response.clone();
              const body = await clone.text();
              writeCachedResponse(url, response, body);
            }
            return response;
          })
          .catch(() => null)
          .finally(() => inflightRequests.delete(cacheKey));

        inflightRequests.set(cacheKey, revalidatePromise);
        return cachedResponse;
      }

      const requestPromise = nativeFetch(resource, options)
        .then(async response => {
          if (response.ok) {
            const clone = response.clone();
            const body = await clone.text();
            writeCachedResponse(url, response, body);
            return new Response(body, {
              status: response.status,
              statusText: response.statusText,
              headers: response.headers
            });
          }
          return response;
        })
        .finally(() => inflightRequests.delete(cacheKey));

      inflightRequests.set(cacheKey, requestPromise);
      const response = await requestPromise;
      return response.clone();
    };
  }

  function optimizeImages() {
    const images = document.querySelectorAll('img');
    images.forEach((img, index) => {
      if (!img.getAttribute('loading')) {
        img.setAttribute('loading', index < 2 ? 'eager' : 'lazy');
      }
      if (!img.getAttribute('decoding')) {
        img.setAttribute('decoding', 'async');
      }
      if (!img.getAttribute('fetchpriority') && index > 1) {
        img.setAttribute('fetchpriority', 'low');
      }
    });
  }

  function optimizeSections() {
    document.querySelectorAll('section, article, aside').forEach((element, index) => {
      if (index > 1 && !element.style.contentVisibility) {
        element.style.contentVisibility = 'auto';
        element.style.containIntrinsicSize = '1px 600px';
      }
    });
  }

  function installIdleSessionTimeout() {
    if (window.__imajinationSessionTimeoutInstalled) return;
    window.__imajinationSessionTimeoutInstalled = true;

    const hasSession = !!localStorage.getItem('userId');
    const path = getCurrentPath();
    if (!hasSession || isAuthPage(path) || !isProtectedPage(path)) return;
    if ((localStorage.getItem('userRole') || '').toLowerCase() === 'organizer') return;

    const resetTimer = () => {
      if (sessionTimeoutHandle) {
        window.clearTimeout(sessionTimeoutHandle);
      }

      sessionTimeoutHandle = window.setTimeout(() => {
        localStorage.clear();
        localStorage.setItem('sessionTimedOut', '1');
        window.location.href = '/pages/auth/login.html?timeout=1';
      }, sessionIdleLimitMs);
    };

    ['click', 'keydown', 'mousemove', 'scroll', 'touchstart'].forEach((eventName) => {
      window.addEventListener(eventName, resetTimer, { passive: true });
    });

    resetTimer();
  }

  function getCurrentPath() {
    return (window.location.pathname || '').toLowerCase();
  }

  function isAuthPage(path = getCurrentPath()) {
    return path.includes('/pages/auth/');
  }

  function isProtectedPage(path = getCurrentPath()) {
    return protectedPathPrefixes.some((prefix) => path.startsWith(prefix));
  }

  function notifySessionStateChanged() {
    window.dispatchEvent(new CustomEvent('imajination:session-cleared'));
  }

  function clearStoredUserSession(reasonKey) {
    localStorage.removeItem('userId');
    localStorage.removeItem('userFirstName');
    localStorage.removeItem('userDisplayName');
    localStorage.removeItem('userRole');
    localStorage.removeItem('username');
    localStorage.removeItem('userProfilePicture');
    localStorage.removeItem('sessionToken');
    localStorage.removeItem('accessToken');
    sessionStorage.removeItem('loginSessionNotice');
    if (reasonKey) {
      localStorage.setItem('sessionEndedReason', reasonKey);
    }
    notifySessionStateChanged();
  }

  function redirectAfterSessionEnded(replacedElsewhere) {
    window.location.href = replacedElsewhere
      ? '/pages/auth/login.html?session=another-device'
      : '/pages/auth/login.html?session=expired';
  }

  function handleEndedSession(replacedElsewhere) {
    const protectedPage = isProtectedPage();
    clearStoredUserSession(protectedPage
      ? (replacedElsewhere ? 'another_device' : 'session_ended')
      : '');
    if (protectedPage) {
      window.setTimeout(() => redirectAfterSessionEnded(replacedElsewhere), 0);
    }
  }

  function buildSessionStatusHeaders() {
    const headers = {
      'X-Requested-With': 'fetch'
    };
    const sessionToken = (localStorage.getItem('sessionToken') || '').trim();
    if (sessionToken) {
      headers['X-Session-Token'] = sessionToken;
    }
    return headers;
  }

  async function confirmSessionExpired() {
    if (sessionValidationPromise) {
      return sessionValidationPromise;
    }

    const userId = (localStorage.getItem('userId') || '').trim();
    if (!userId) {
      return true;
    }

    const nativeFetch = window.__imajinationNativeFetch || window.fetch.bind(window);
    sessionValidationPromise = nativeFetch(`/api/auth/session-status?userId=${encodeURIComponent(userId)}`, {
      method: 'GET',
      cache: 'no-store',
      credentials: 'same-origin',
      headers: buildSessionStatusHeaders()
    })
      .then(async (validationResponse) => {
        if (validationResponse.ok) {
          return false;
        }

        if (validationResponse.status !== 401) {
          return false;
        }

        const data = await validationResponse.json().catch(() => ({}));
        return {
          expired: true,
          replacedElsewhere: !!data?.replacedElsewhere
        };
      })
      .catch(() => false)
      .finally(() => {
        sessionValidationPromise = null;
      });

    return sessionValidationPromise;
  }

  async function handleUnauthorizedApiResponse(response, requestUrl) {
    const path = getCurrentPath();
    if (isAuthPage(path) || response?.status !== 401) {
      return response;
    }

    const url = String(requestUrl || '').toLowerCase();
    if (url.includes('/api/auth/login')
      || url.includes('/api/auth/google-login')
      || url.includes('/api/auth/mfa/')
      || url.includes('/api/auth/session-status')) {
      return response;
    }

    if (!localStorage.getItem('userId')) {
      return response;
    }

    const sessionState = await confirmSessionExpired();
    if (!sessionState || sessionState === false) {
      return response;
    }

    const replacedElsewhere = typeof sessionState === 'object' && !!sessionState.replacedElsewhere;
    handleEndedSession(replacedElsewhere);
    return response;
  }

  function installSessionMonitor() {
    if (window.__imajinationSessionMonitorInstalled) return;
    window.__imajinationSessionMonitorInstalled = true;

    const path = getCurrentPath();
    if (isAuthPage(path) || !isProtectedPage(path)) return;

    const userId = (localStorage.getItem('userId') || '').trim();
    if (!userId) return;
    if ((localStorage.getItem('userRole') || '').toLowerCase() === 'organizer') return;

    const nativeFetch = window.__imajinationNativeFetch || window.fetch.bind(window);

    const validateSession = async () => {
      try {
        const response = await nativeFetch(`/api/auth/session-status?userId=${encodeURIComponent(userId)}`, {
          method: 'GET',
          cache: 'no-store',
          credentials: 'same-origin',
          headers: buildSessionStatusHeaders()
        });

        if (response.ok) {
          return;
        }

        if (response.status !== 401) {
          return;
        }

        const data = await response.json().catch(() => ({}));
        const replacedElsewhere = !!data?.replacedElsewhere;
        handleEndedSession(replacedElsewhere);
      } catch {
        // Keep the current session active if the network briefly fails.
      }
    };

    validateSession();

    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        validateSession();
      }
    });

    window.addEventListener('focus', validateSession);
    window.addEventListener('pageshow', validateSession);

    sessionMonitorHandle = window.setInterval(validateSession, sessionMonitorIntervalMs);
  }

  function installMobileNav() {
    // Only inject on pages that have the standard public nav link pattern
    const navLinks = document.querySelector('.detail-nav-link, nav .hidden.md\\:flex');
    if (!navLinks) return;

    const navBar = document.querySelector('nav > div.absolute + div, nav > div:last-child, nav');
    const navInner = document.querySelector('nav .relative.max-w-7xl, nav .relative');
    if (!navInner) return;

    // Don't inject on dashboard pages (they have their own nav)
    if (document.querySelector('.mobile-dashboard-shell')) return;
    if (document.querySelector('.admin-section')) return;

    // Collect nav links from hidden desktop nav
    const desktopNav = document.querySelector('.hidden.md\\:flex');
    if (!desktopNav) return;

    const links = Array.from(desktopNav.querySelectorAll('a[href]'));
    if (!links.length) return;

    // Build hamburger button
    const hamburger = document.createElement('button');
    hamburger.setAttribute('type', 'button');
    hamburger.setAttribute('aria-label', 'Open navigation menu');
    hamburger.className = 'md:hidden flex items-center justify-center w-9 h-9 rounded-xl bg-white/5 border border-white/10 text-white/70 hover:text-white hover:bg-white/10 transition-colors';
    hamburger.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="3" y1="6" x2="21" y2="6"/><line x1="3" y1="12" x2="21" y2="12"/><line x1="3" y1="18" x2="21" y2="18"/></svg>';

    // Insert hamburger before the right-side buttons
    const rightSection = navInner.querySelector('.flex.items-center.gap-3.relative, .flex.items-center.gap-4');
    if (rightSection) {
      rightSection.insertBefore(hamburger, rightSection.firstChild);
    }

    // Build mobile menu overlay
    const menu = document.createElement('div');
    menu.className = 'mobile-nav-menu';
    menu.setAttribute('id', 'mobileNavMenu');
    menu.innerHTML = `
      <button class="mobile-nav-close-btn" id="mobileNavClose" aria-label="Close menu">
        <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
      </button>
      <div class="flex items-center gap-3 mb-6 mt-2">
        <img src="/assets/images/logo.png" alt="Tugs!" class="h-7 object-contain object-left" onerror="this.style.display='none'">
      </div>
      <nav>
        ${links.map(link => `<a href="${link.href}" class="${link.className.includes('active') ? 'active-mobile' : ''}">${link.textContent.trim()}</a>`).join('')}
      </nav>
    `;
    document.body.appendChild(menu);

    const openMenu = () => menu.classList.add('open');
    const closeMenu = () => menu.classList.remove('open');

    hamburger.addEventListener('click', openMenu);
    document.getElementById('mobileNavClose')?.addEventListener('click', closeMenu);
    menu.addEventListener('click', (e) => { if (e.target === menu) closeMenu(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeMenu(); });

    // Close menu when a link is clicked
    menu.querySelectorAll('nav a').forEach(a => a.addEventListener('click', closeMenu));
  }

  function initPerformanceHelpers() {
    installSecurityFetch();
    installFetchOptimizer();
    installIdleSessionTimeout();
    installSessionMonitor();
    optimizeImages();
    optimizeSections();
    installMobileNav();
  }

  window.initImajinationPerformanceHelpers = initPerformanceHelpers;

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initPerformanceHelpers, { once: true });
  } else {
    initPerformanceHelpers();
  }
})();
